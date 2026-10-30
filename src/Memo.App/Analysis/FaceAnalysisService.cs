using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Memo.Core;
using Memo.Media;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using WinDetectedFace = Windows.Media.FaceAnalysis.DetectedFace;
using WinFaceDetector = Windows.Media.FaceAnalysis.FaceDetector;

namespace Memo.App.Analysis;

/// <summary>Rectangle normalisé (0..1) dans le sens d'affichage de l'image.</summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height);

/// <summary>
/// Visage détecté. <see cref="Bounds"/> est normalisé ; <see cref="X"/>, <see cref="Y"/>,
/// <see cref="Width"/> et <see cref="Height"/> sont en pixels de l'image dans son sens d'affichage
/// (orientation EXIF appliquée), prêts pour <c>AnalysisStore.AddFaceAsync</c>.
/// </summary>
public sealed record DetectedFace(
    NormalizedRect Bounds,
    ulong Descriptor,
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>Résultat d'une détection sur une image.</summary>
public sealed record FaceDetectionResult(
    IReadOnlyList<DetectedFace> Faces,
    int Width,
    int Height,
    TimeSpan Duration,
    string? Error,
    bool Cancelled = false,
    bool DetectorUnavailable = false);

/// <summary>Disponibilité du détecteur de visages Windows.</summary>
public sealed record FaceDetectorAvailability(bool IsAvailable, string? Message);

/// <summary>Étapes rapportées pendant la reconstruction des groupes de visages.</summary>
public enum FaceGroupingPhase
{
    Preparing,
    Detecting,
    Grouping,
    Saving,
    Done,
}

/// <summary>Progression d'une reconstruction de groupes (faits sur total).</summary>
public sealed record FaceGroupingProgress(FaceGroupingPhase Phase, int Done, int Total, string Message);

/// <summary>Bilan d'une reconstruction de groupes de visages.</summary>
public sealed record FaceGroupingResult(
    int ImagesScanned,
    int ImagesWithFaces,
    int FacesDetected,
    int GroupsCreated,
    int FacesGrouped,
    int FailedImages,
    TimeSpan Duration,
    bool Cancelled,
    string? Error,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Détection de visages locale avec <c>Windows.Media.FaceAnalysis.FaceDetector</c> et
/// regroupement par empreinte perceptuelle du visage.
///
/// Chaque visage reçoit un descripteur dHash 64 bits calculé sur un carré centré autour du cadre.
/// Deux visages dont les descripteurs diffèrent de 12 bits au plus (sur 64) sont rapprochés. Ce
/// descripteur décrit l'apparence du cadrage, pas l'identité d'une personne : les groupes sont des
/// propositions à corriger, jamais une reconnaissance certaine. Aucun groupe n'est nommé
/// automatiquement.
///
/// L'analyse est facultative : si le module Windows est absent (édition N, composant désactivé),
/// les méthodes retournent un diagnostic en français au lieu de laisser remonter une exception.
/// </summary>
public sealed class FaceAnalysisService
{
    /// <summary>Plus grand côté analysé. Au-delà, le détecteur ne gagne presque rien en précision.</summary>
    public const int MaxAnalysisDimension = 960;

    /// <summary>Distance de Hamming maximale entre deux descripteurs d'un même groupe.</summary>
    public const int DefaultMaxDistance = 12;

    /// <summary>Un groupe de personnes contient au moins deux visages.</summary>
    public const int MinimumGroupSize = 2;

    /// <summary>Nombre maximal de diagnostics conservés pour ne pas saturer l'interface.</summary>
    private const int MaxDiagnostics = 30;

    /// <summary>Marge autour du cadre du visage pour le descripteur (12 % de chaque côté).</summary>
    private const double DescriptorPadding = 0.12;

    private const string DetectorUnavailableMessage =
        "La détection de visages Windows n'est pas disponible sur cette installation. " +
        "Elle dépend du module « Analyse des visages » de Windows Media Foundation, absent des " +
        "éditions N ou désactivé. Installez le Media Feature Pack de Windows, ou laissez l'option " +
        "désactivée : le reste de Memo continue de fonctionner.";

    private readonly SemaphoreSlim _detectorGate = new(1, 1);
    private WinFaceDetector? _detector;
    private string? _detectorError;
    private bool _probed;

    /// <summary>
    /// Vérifie si le détecteur peut être créé. Le résultat est mémorisé par instance ; une nouvelle
    /// instance refait le test.
    /// </summary>
    public async Task<FaceDetectorAvailability> GetAvailabilityAsync(CancellationToken ct = default)
    {
        await _detectorGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var detector = await GetOrCreateDetectorAsync(ct).ConfigureAwait(false);
            return detector is null
                ? new FaceDetectorAvailability(false, _detectorError ?? DetectorUnavailableMessage)
                : new FaceDetectorAvailability(true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Un type WinRT absent (édition N) peut échouer avant même l'entrée dans
            // GetOrCreateDetectorAsync : l'écran de réglages doit tout de même obtenir un message.
            return new FaceDetectorAvailability(
                false,
                $"{DetectorUnavailableMessage} Détail technique : {ex.Message}");
        }
        finally
        {
            _detectorGate.Release();
        }
    }

    /// <summary>Raccourci statique pour un écran de réglages qui ne garde pas d'instance.</summary>
    public static async Task<FaceDetectorAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        var service = new FaceAnalysisService();
        return await service.GetAvailabilityAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Détecte les visages d'un fichier image. Les erreurs de décodage, l'absence du détecteur et
    /// l'annulation sont retournées dans le résultat ; aucune exception n'est levée pour une image
    /// illisible ou un composant Windows manquant.
    /// </summary>
    public async Task<FaceDetectionResult> DetectAsync(string path, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure("Aucun chemin d'image n'a été fourni pour la détection de visages.", stopwatch.Elapsed);
        }

        if (ct.IsCancellationRequested)
        {
            return Cancelled(stopwatch.Elapsed);
        }

        PreparedFaceImage? prepared = null;
        try
        {
            // Le décodage et la préparation Skia sont synchrones : ils ne doivent pas bloquer l'UI.
            prepared = await Task.Run(() => Prepare(path), ct).ConfigureAwait(false);
            var analyzed = prepared.Analyzed;

            // Le détecteur Windows exige une image suffisamment grande ; une vignette minuscule
            // n'est pas une erreur, elle ne contient simplement aucun visage exploitable.
            if (analyzed.Width < 32 || analyzed.Height < 32)
            {
                return new FaceDetectionResult(
                    Array.Empty<DetectedFace>(),
                    prepared.OriginalWidth,
                    prepared.OriginalHeight,
                    stopwatch.Elapsed,
                    null);
            }

            IList<WinDetectedFace>? detected;
            string? detectorError = null;
            bool unavailable = false;

            await _detectorGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var detector = await GetOrCreateDetectorAsync(ct).ConfigureAwait(false);
                if (detector is null)
                {
                    unavailable = true;
                    detected = null;
                    detectorError = _detectorError ?? DetectorUnavailableMessage;
                }
                else
                {
                    detected = await DetectOnceAsync(detector, analyzed, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _detectorGate.Release();
            }

            if (unavailable)
            {
                return new FaceDetectionResult(
                    Array.Empty<DetectedFace>(),
                    prepared.OriginalWidth,
                    prepared.OriginalHeight,
                    stopwatch.Elapsed,
                    detectorError,
                    DetectorUnavailable: true);
            }

            var faces = BuildDetectedFaces(detected ?? Array.Empty<WinDetectedFace>(), prepared);
            return new FaceDetectionResult(
                faces,
                prepared.OriginalWidth,
                prepared.OriginalHeight,
                stopwatch.Elapsed,
                null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(stopwatch.Elapsed);
        }
        catch (MediaDecodeException ex)
        {
            return Failure($"Détection de visages impossible pour « {path} » : {ex.Message}", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failure(
                $"La détection de visages a échoué pour « {path} » : {ex.Message}",
                stopwatch.Elapsed);
        }
        finally
        {
            prepared?.Dispose();
        }
    }

    /// <summary>
    /// Reconstruit les visages et les groupes d'une bibliothèque à partir de fichiers fournis.
    /// Les détections existantes sont effacées au début (les noms de groupes créés par
    /// l'utilisateur, eux, restent dans le catalogue). Chaque visage est ajouté avec son
    /// descripteur ; les groupes de deux visages ou plus, issus d'au moins deux souvenirs
    /// différents, sont créés sans nom et reçoivent leurs visages via <c>AssignFace</c>.
    ///
    /// Les fichiers sont traités un par un. Une erreur sur une image devient un diagnostic et
    /// n'interrompt pas la passe. Une annulation laisse les visages déjà écrits en base.
    /// </summary>
    public async Task<FaceGroupingResult> RebuildGroupsAsync(
        MemoLibrary library,
        IReadOnlyList<(long MemoryId, string Path)> sources,
        IProgress<FaceGroupingProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(sources);

        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<string>();
        var detections = new List<(long MemoryId, DetectedFace Face)>(sources.Count);
        int scanned = 0;
        int imagesWithFaces = 0;
        int failed = 0;
        int groupsCreated = 0;
        int facesGrouped = 0;

        try
        {
            // La disponibilité est testée avant d'effacer les détections existantes : une machine
            // sans module de visages ne doit pas perdre les groupes déjà calculés.
            var availability = await GetAvailabilityAsync(ct).ConfigureAwait(false);
            if (!availability.IsAvailable)
            {
                return new FaceGroupingResult(
                    0, 0, 0, 0, 0, 0, stopwatch.Elapsed, false,
                    availability.Message ?? DetectorUnavailableMessage,
                    diagnostics);
            }

            progress?.Report(new FaceGroupingProgress(
                FaceGroupingPhase.Preparing, 0, sources.Count,
                "Préparation des visages…"));

            var seenMemories = new HashSet<long>();

            foreach (var (memoryId, path) in sources)
            {
                ct.ThrowIfCancellationRequested();
                if (memoryId <= 0 || string.IsNullOrWhiteSpace(path) || !seenMemories.Add(memoryId))
                {
                    continue;
                }

                scanned++;
                string name = DisplayName(path);
                progress?.Report(new FaceGroupingProgress(
                    FaceGroupingPhase.Detecting, scanned, sources.Count,
                    $"Visages {scanned}/{sources.Count} — {name}"));

                var detection = await DetectAsync(path, ct).ConfigureAwait(false);
                if (detection.Cancelled)
                {
                    return new FaceGroupingResult(
                        scanned, imagesWithFaces, detections.Count, 0, 0, failed,
                        stopwatch.Elapsed, true, "Analyse annulée.", diagnostics);
                }

                if (detection.DetectorUnavailable)
                {
                    return new FaceGroupingResult(
                        scanned, imagesWithFaces, detections.Count, 0, 0, failed,
                        stopwatch.Elapsed, false,
                        detection.Error ?? DetectorUnavailableMessage, diagnostics);
                }

                if (detection.Error is not null)
                {
                    failed++;
                    AddDiagnostic(diagnostics, detection.Error);
                    continue;
                }

                if (detection.Faces.Count > 0)
                {
                    imagesWithFaces++;
                    foreach (var face in detection.Faces)
                    {
                        detections.Add((memoryId, face));
                    }
                }
            }

            if (detections.Count == 0)
            {
                // Zéro visage sur des images toutes lues est un résultat valide : les anciennes
                // détections sont devenues obsolètes. En revanche, si des images ont échoué, on
                // conserve les détections précédentes plutôt que de les perdre sans remplacement.
                if (failed == 0)
                {
                    await library.Analysis.ClearFacesAsync(ct).ConfigureAwait(false);
                }

                progress?.Report(new FaceGroupingProgress(
                    FaceGroupingPhase.Done, scanned, sources.Count,
                    "Aucun visage détecté."));
                return new FaceGroupingResult(
                    scanned, imagesWithFaces, 0, 0, 0, failed,
                    stopwatch.Elapsed, false, null, diagnostics);
            }

            progress?.Report(new FaceGroupingProgress(
                FaceGroupingPhase.Grouping, 0, detections.Count,
                $"Regroupement de {detections.Count} visage(s)…"));

            var descriptors = detections.Select(static pair => (pair.MemoryId, pair.Face.Descriptor)).ToList();
            var clusters = FaceHammingClusterer.Cluster(descriptors, DefaultMaxDistance, MinimumGroupSize);

            // Le remplacement n'a lieu qu'une fois la détection terminée : une annulation ou une
            // panne du détecteur pendant la lecture laisse intactes les détections précédentes.
            // Les groupes nommés par l'utilisateur ne sont pas supprimés par ClearFacesAsync :
            // ils restent visibles, même vides.
            await library.Analysis.ClearFacesAsync(ct).ConfigureAwait(false);

            progress?.Report(new FaceGroupingProgress(
                FaceGroupingPhase.Saving, 0, detections.Count,
                "Enregistrement des visages…"));

            // Tous les visages sont conservés, même isolés : ils pourront être nommés ou fusionnés
            // plus tard par l'utilisateur. Seuls les groupes de taille suffisante sont assignés.
            var faceIds = new long?[detections.Count];
            int saved = 0;
            for (int index = 0; index < detections.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var (memoryId, face) = detections[index];
                try
                {
                    faceIds[index] = await library.Analysis.AddFaceAsync(
                        memoryId,
                        face.X,
                        face.Y,
                        face.Width,
                        face.Height,
                        groupId: null,
                        descriptor: DescriptorToBytes(face.Descriptor),
                        ct: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AddDiagnostic(diagnostics, $"Visage non enregistré pour le souvenir {memoryId} : {ex.Message}");
                }

                saved++;
                progress?.Report(new FaceGroupingProgress(
                    FaceGroupingPhase.Saving, saved, detections.Count,
                    $"Enregistrement {saved}/{detections.Count}…"));
            }

            foreach (var cluster in clusters)
            {
                ct.ThrowIfCancellationRequested();

                // Une grappe dont aucun visage n'a pu être écrit ne doit pas laisser un groupe vide.
                if (!cluster.Any(index => faceIds[index] is not null))
                {
                    continue;
                }

                long groupId;
                try
                {
                    groupId = await library.Analysis.CreatePersonGroupAsync(null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AddDiagnostic(diagnostics, $"Groupe de visages non créé : {ex.Message}");
                    continue;
                }

                groupsCreated++;
                foreach (int index in cluster)
                {
                    if (faceIds[index] is not { } faceId)
                    {
                        continue;
                    }

                    try
                    {
                        await library.Analysis.AssignFaceAsync(faceId, groupId, ct).ConfigureAwait(false);
                        facesGrouped++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        AddDiagnostic(diagnostics, $"Visage {faceId} non assigné au groupe {groupId} : {ex.Message}");
                    }
                }
            }

            progress?.Report(new FaceGroupingProgress(
                FaceGroupingPhase.Done, scanned, sources.Count,
                $"Visages : {detections.Count} détecté(s), {groupsCreated} groupe(s)."));

            return new FaceGroupingResult(
                scanned, imagesWithFaces, detections.Count, groupsCreated, facesGrouped, failed,
                stopwatch.Elapsed, false, null, diagnostics);
        }
        catch (OperationCanceledException)
        {
            return new FaceGroupingResult(
                scanned, imagesWithFaces, detections.Count, groupsCreated, facesGrouped, failed,
                stopwatch.Elapsed, true, "Analyse annulée.", diagnostics);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new FaceGroupingResult(
                scanned, imagesWithFaces, detections.Count, groupsCreated, facesGrouped, failed,
                stopwatch.Elapsed, false,
                $"La reconstruction des groupes de visages a échoué : {ex.Message}",
                diagnostics);
        }
    }

    /// <summary>Sérialise un descripteur en 8 octets big-endian, stable entre machines.</summary>
    public static byte[] DescriptorToBytes(ulong descriptor)
    {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, descriptor);
        return bytes;
    }

    /// <summary>Relit un descripteur écrit par <see cref="DescriptorToBytes"/>.</summary>
    public static ulong DescriptorFromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != sizeof(ulong))
        {
            throw new ArgumentException("Un descripteur de visage occupe exactement 8 octets.", nameof(bytes));
        }

        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    private async Task<WinFaceDetector?> GetOrCreateDetectorAsync(CancellationToken ct)
    {
        if (_detector is not null)
        {
            return _detector;
        }

        if (_probed)
        {
            return null;
        }

        _probed = true;
        try
        {
            // IsSupported évite de tenter CreateAsync sur une édition Windows sans le composant.
            if (!WinFaceDetector.IsSupported)
            {
                _detectorError = DetectorUnavailableMessage;
                return null;
            }

            _detector = await WinFaceDetector.CreateAsync().AsTask(ct).ConfigureAwait(false);
            if (_detector is null)
            {
                _detectorError = DetectorUnavailableMessage;
            }
        }
        catch (OperationCanceledException)
        {
            _probed = false;
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _detectorError =
                $"{DetectorUnavailableMessage} Détail technique : {ex.Message}";
        }

        return _detector;
    }

    private static async Task<IList<WinDetectedFace>> DetectOnceAsync(
        WinFaceDetector detector,
        SKBitmap analyzed,
        CancellationToken ct)
    {
        // L'API Windows n'accepte aujourd'hui que Gray8 ou NV12. Gray8 est le plus simple à
        // produire depuis SkiaSharp et reste identique d'une version de Windows à l'autre.
        byte[] gray = AnalysisImage.ToGray8Bytes(analyzed);
        using var grayBitmap = CreateSoftwareBitmap(
            gray, BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore, analyzed.Width, analyzed.Height);
        return await detector.DetectFacesAsync(grayBitmap).AsTask(ct).ConfigureAwait(false);
    }

    private static SoftwareBitmap CreateSoftwareBitmap(
        byte[] pixels,
        BitmapPixelFormat format,
        BitmapAlphaMode alphaMode,
        int width,
        int height)
    {
        var buffer = CryptographicBuffer.CreateFromByteArray(pixels);
        return SoftwareBitmap.CreateCopyFromBuffer(buffer, format, width, height, alphaMode);
    }

    private static IReadOnlyList<DetectedFace> BuildDetectedFaces(
        IList<WinDetectedFace> detected,
        PreparedFaceImage prepared)
    {
        var analyzed = prepared.Analyzed;
        var faces = new List<DetectedFace>(detected.Count);
        foreach (var face in detected)
        {
            var box = face.FaceBox;
            int x = (int)Math.Min(box.X, (uint)Math.Max(0, analyzed.Width - 1));
            int y = (int)Math.Min(box.Y, (uint)Math.Max(0, analyzed.Height - 1));
            int width = (int)Math.Min(box.Width, (uint)(analyzed.Width - x));
            int height = (int)Math.Min(box.Height, (uint)(analyzed.Height - y));
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            var region = AnalysisImage.SquareRegion(
                analyzed.Width,
                analyzed.Height,
                x,
                y,
                width,
                height,
                DescriptorPadding);
            ulong descriptor = VisualHashService.ComputeFromRegion(analyzed, region);

            double normalizedX = x / (double)analyzed.Width;
            double normalizedY = y / (double)analyzed.Height;
            double normalizedWidth = width / (double)analyzed.Width;
            double normalizedHeight = height / (double)analyzed.Height;

            int originalX = (int)Math.Round(normalizedX * prepared.OriginalWidth);
            int originalY = (int)Math.Round(normalizedY * prepared.OriginalHeight);
            int originalWidth = Math.Max(1, (int)Math.Round(normalizedWidth * prepared.OriginalWidth));
            int originalHeight = Math.Max(1, (int)Math.Round(normalizedHeight * prepared.OriginalHeight));
            originalX = Math.Clamp(originalX, 0, Math.Max(0, prepared.OriginalWidth - 1));
            originalY = Math.Clamp(originalY, 0, Math.Max(0, prepared.OriginalHeight - 1));
            originalWidth = Math.Clamp(originalWidth, 1, prepared.OriginalWidth - originalX);
            originalHeight = Math.Clamp(originalHeight, 1, prepared.OriginalHeight - originalY);

            faces.Add(new DetectedFace(
                new NormalizedRect(
                    Math.Clamp(normalizedX, 0.0, 1.0),
                    Math.Clamp(normalizedY, 0.0, 1.0),
                    Math.Clamp(normalizedWidth, 0.0, 1.0),
                    Math.Clamp(normalizedHeight, 0.0, 1.0)),
                descriptor,
                originalX,
                originalY,
                originalWidth,
                originalHeight));
        }

        faces.Sort(static (left, right) =>
        {
            int byY = left.Y.CompareTo(right.Y);
            return byY != 0 ? byY : left.X.CompareTo(right.X);
        });
        return faces;
    }

    private static PreparedFaceImage Prepare(string path)
    {
        using var decoded = ImageDecoder.Decode(path);
        int originalWidth = decoded.Bitmap.Width;
        int originalHeight = decoded.Bitmap.Height;
        var analyzed = AnalysisImage.RenderOpaque(decoded.Bitmap, MaxAnalysisDimension);
        return new PreparedFaceImage(analyzed, originalWidth, originalHeight);
    }

    private static FaceDetectionResult Failure(string error, TimeSpan duration, bool unavailable = false) =>
        new(Array.Empty<DetectedFace>(), 0, 0, duration, error, false, unavailable);

    private static FaceDetectionResult Cancelled(TimeSpan duration) =>
        new(Array.Empty<DetectedFace>(), 0, 0, duration, "Analyse annulée.", true);

    private static string DisplayName(string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static void AddDiagnostic(List<string> diagnostics, string message)
    {
        if (diagnostics.Count < MaxDiagnostics)
        {
            diagnostics.Add(message);
        }
    }

    private sealed class PreparedFaceImage : IDisposable
    {
        public PreparedFaceImage(SKBitmap analyzed, int originalWidth, int originalHeight)
        {
            Analyzed = analyzed;
            OriginalWidth = originalWidth;
            OriginalHeight = originalHeight;
        }

        public SKBitmap Analyzed { get; }

        public int OriginalWidth { get; }

        public int OriginalHeight { get; }

        public void Dispose() => Analyzed.Dispose();
    }
}

/// <summary>
/// Regroupement par distance de Hamming sur des descripteurs 64 bits. Un arbre BK évite de
/// comparer chaque visage à tous les précédents ; les unions sont ensuite consolidées par un
/// ensemble disjoint. Un groupe doit contenir au moins <c>minimumSize</c> visages issus d'au moins
/// deux souvenirs différents.
/// </summary>
internal static class FaceHammingClusterer
{
    public static IReadOnlyList<IReadOnlyList<int>> Cluster(
        IReadOnlyList<(long MemoryId, ulong Descriptor)> items,
        int maxDistance,
        int minimumSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (maxDistance < 0 || maxDistance > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "La distance doit être comprise entre 0 et 64.");
        }

        int count = items.Count;
        if (count < Math.Max(2, minimumSize))
        {
            return Array.Empty<IReadOnlyList<int>>();
        }

        var parent = new int[count];
        for (int index = 0; index < count; index++)
        {
            parent[index] = index;
        }

        var indexTree = new HammingIndex();
        var matches = new List<int>();
        for (int index = 0; index < count; index++)
        {
            matches.Clear();
            indexTree.Search(items[index].Descriptor, maxDistance, matches);
            foreach (int match in matches)
            {
                Union(parent, index, match);
            }

            indexTree.Add(items[index].Descriptor, index);
        }

        var groups = new Dictionary<int, List<int>>();
        for (int index = 0; index < count; index++)
        {
            int root = Find(parent, index);
            if (!groups.TryGetValue(root, out var members))
            {
                members = new List<int>();
                groups.Add(root, members);
            }

            members.Add(index);
        }

        var result = new List<IReadOnlyList<int>>();
        foreach (var members in groups.Values)
        {
            if (members.Count < Math.Max(2, minimumSize))
            {
                continue;
            }

            var memories = new HashSet<long>();
            foreach (int member in members)
            {
                memories.Add(items[member].MemoryId);
            }

            if (memories.Count >= 2)
            {
                result.Add(members);
            }
        }

        return result;
    }

    private static int Find(int[] parent, int index)
    {
        int root = index;
        while (parent[root] != root)
        {
            root = parent[root];
        }

        while (parent[index] != index)
        {
            int next = parent[index];
            parent[index] = root;
            index = next;
        }

        return root;
    }

    private static void Union(int[] parent, int left, int right)
    {
        int leftRoot = Find(parent, left);
        int rightRoot = Find(parent, right);
        if (leftRoot != rightRoot)
        {
            parent[rightRoot] = leftRoot;
        }
    }

    /// <summary>Arbre BK sur 64 bits : recherche exacte des voisins dans un rayon de Hamming.</summary>
    private sealed class HammingIndex
    {
        private Node? _root;

        public void Add(ulong descriptor, int index)
        {
            if (_root is null)
            {
                _root = new Node(descriptor, index);
                return;
            }

            var node = _root;
            while (true)
            {
                int distance = BitOperations.PopCount(descriptor ^ node.Descriptor);
                if (node.Children[distance] is { } child)
                {
                    node = child;
                }
                else
                {
                    node.Children[distance] = new Node(descriptor, index);
                    return;
                }
            }
        }

        public void Search(ulong descriptor, int radius, List<int> results)
        {
            if (_root is null)
            {
                return;
            }

            var stack = new Stack<Node>();
            stack.Push(_root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                int distance = BitOperations.PopCount(descriptor ^ node.Descriptor);
                if (distance <= radius)
                {
                    results.Add(node.Index);
                }

                int low = Math.Max(0, distance - radius);
                int high = Math.Min(64, distance + radius);
                for (int childDistance = low; childDistance <= high; childDistance++)
                {
                    if (node.Children[childDistance] is { } child)
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        private sealed class Node
        {
            public Node(ulong descriptor, int index)
            {
                Descriptor = descriptor;
                Index = index;
            }

            public ulong Descriptor { get; }

            public int Index { get; }

            public Node?[] Children { get; } = new Node?[65];
        }
    }
}
