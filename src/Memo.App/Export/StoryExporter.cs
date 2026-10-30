using System.Globalization;
using System.Runtime.InteropServices;
using Memo.Core;
using Memo.Media;
using SkiaSharp;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Memo.App.Export;

/// <summary>
/// Export d'une Story (ou d'un récapitulatif) en un MP4 H.264/AAC, sans FFmpeg.
///
/// Le rendu s'appuie sur <see cref="MediaComposition"/> et Media Foundation : les photos sont
/// transformées en clips de durée fixe, les vidéos conservent leur durée et leur audio, et les
/// calques sont ajoutés dans une couche <see cref="MediaOverlay"/> positionnée sur la zone
/// d'affichage réelle du média (letterbox), sans déformation.
///
/// Le fichier est d'abord écrit sous un nom temporaire dans le dossier de destination, puis
/// renommé atomiquement. Aucun fichier final n'est annoncé comme réussi tant que sa taille n'a
/// pas été vérifiée. En cas d'échec ou d'annulation, le temporaire est supprimé.
/// </summary>
public sealed class StoryExporter
{
    private const int VideoFrameRate = 30;
    private const uint AudioBitrate = 192_000;
    private const long DiskSafetyMarginBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Exporte la Story décrite par <paramref name="request"/>. Les erreurs prévisibles
    /// (fichier existant, codec absent, espace insuffisant, source illisible) sont retournées
    /// dans <see cref="StoryExportResult"/> avec un message lisible ; elles ne lèvent pas
    /// d'exception. Seul un argument nul lève une exception de programmation.
    /// </summary>
    public async Task<StoryExportResult> ExportAsync(
        StoryExportRequest request,
        IProgress<StoryExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var warnings = new List<string>();
        var scratch = new ScratchSpace();
        string? temporaryMp4Path = null;
        TimeSpan plannedDuration = TimeSpan.Zero;
        string? outputFullPath = null;
        int itemCount = request.Items?.Count ?? 0;

        void Report(
            StoryExportPhase phase,
            double percent,
            string? currentItem,
            int itemIndex,
            string message)
        {
            if (progress is null)
            {
                return;
            }

            try
            {
                progress.Report(new StoryExportProgress(
                    phase,
                    double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0,
                    currentItem,
                    itemIndex,
                    itemCount,
                    message));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Une fenêtre fermée ne doit pas interrompre un rendu déjà lancé.
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(StoryExportPhase.Preparing, 0, null, 0, "Validation de la demande d'export.");

            string? validationError = Validate(request, out outputFullPath, out itemCount);
            if (validationError is not null)
            {
                Report(StoryExportPhase.Failed, 0, null, 0, validationError);
                return StoryExportResult.Failure(validationError, warnings);
            }

            string outputDirectory = Path.GetDirectoryName(outputFullPath!)!;
            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new StoryExportException(
                    $"Le dossier de sortie « {outputDirectory} » n'a pas pu être créé : {Describe(ex)}",
                    ex);
            }

            // Estimation volontairement simple avant d'ouvrir les médias. Une seconde vérification
            // plus précise aura lieu quand les durées réelles des clips seront connues.
            double estimatedSeconds = EstimateDurationSeconds(request);
            EnsureFreeSpace(
                outputDirectory,
                estimatedSeconds,
                request.Quality,
                request.IncludeAudio,
                warnings);

            var prepared = new List<PreparedItem>(request.Items?.Count ?? 0);
            IReadOnlyList<StoryExportItem> items = request.Items ?? Array.Empty<StoryExportItem>();
            var composition = new MediaComposition();
            if (!string.IsNullOrWhiteSpace(request.Title))
            {
                // MediaComposition conserve ce libellé dans son projet, mais Media Foundation ne
                // l'écrit pas dans les métadonnées du MP4 (voir README).
                composition.UserData["Title"] = request.Title.Trim();
            }

            TimeSpan cursor = TimeSpan.Zero;
            for (int index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StoryExportItem item = items[index];
                string mainPath = Path.GetFullPath(item.MainPath.Trim());
                int number = index + 1;

                Report(
                    StoryExportPhase.Preparing,
                    1 + (18.0 * index / items.Count),
                    mainPath,
                    number,
                    $"Préparation de l'élément {number} sur {items.Count}.");

                MediaClip clip;
                if (item.MediaKind == MediaKind.Photo)
                {
                    var photoDuration = TimeSpan.FromSeconds(request.PhotoDurationSeconds);
                    clip = await CreateImageClipAsync(
                            mainPath,
                            photoDuration,
                            scratch,
                            warnings,
                            $"élément {number}",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    clip = await CreateVideoClipAsync(
                            mainPath,
                            warnings,
                            $"élément {number}",
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (!request.IncludeAudio)
                    {
                        // Volume à zéro : la piste audio du clip est ignorée au rendu. On ne touche
                        // pas aux fichiers sources.
                        clip.Volume = 0.0;
                    }
                }

                TimeSpan clipDuration = clip.TrimmedDuration;
                if (clipDuration <= TimeSpan.Zero)
                {
                    clipDuration = item.MediaKind == MediaKind.Photo
                        ? TimeSpan.FromSeconds(request.PhotoDurationSeconds)
                        : clip.OriginalDuration;
                }

                if (clipDuration <= TimeSpan.Zero)
                {
                    throw new StoryExportException(
                        $"La vidéo « {mainPath} » (élément {number}) a une durée nulle ou inconnue ; " +
                        "Media Foundation ne peut pas l'inclure dans la Story.");
                }

                string? overlayPath = string.IsNullOrWhiteSpace(item.OverlayPath)
                    ? null
                    : Path.GetFullPath(item.OverlayPath.Trim());

                int mainWidth;
                int mainHeight;
                (mainWidth, mainHeight) = ResolveMainDimensions(item, mainPath, clip);

                int overlayWidth = 0;
                int overlayHeight = 0;
                if (overlayPath is not null)
                {
                    (overlayWidth, overlayHeight) = ResolveOverlayDimensions(overlayPath, number);
                    if (mainWidth <= 0 || mainHeight <= 0)
                    {
                        throw new StoryExportException(
                            $"Le calque de l'élément {number} ne peut pas être placé : les dimensions " +
                            $"d'affichage du média « {mainPath} » sont inconnues. Renseignez Width et Height " +
                            "dans StoryExportItem.");
                    }
                }

                composition.Clips.Add(clip);
                prepared.Add(new PreparedItem(
                    number,
                    clip,
                    cursor,
                    clipDuration,
                    mainPath,
                    overlayPath,
                    mainWidth,
                    mainHeight,
                    overlayWidth,
                    overlayHeight));

                cursor += clipDuration;
            }

            if (prepared.Count == 0)
            {
                throw new StoryExportException("Aucun média n'a pu être préparé pour l'export.");
            }

            plannedDuration = composition.Duration;
            if (plannedDuration <= TimeSpan.Zero)
            {
                plannedDuration = cursor;
            }

            // Paysage uniquement si tous les éléments ont des dimensions connues et sont
            // effectivement plus larges que hauts. Un cas inconnu reste en portrait, valeur sûre
            // pour une Story Snapchat.
            bool landscape = prepared.All(item =>
                item.MainWidth > 0 &&
                item.MainHeight > 0 &&
                item.MainWidth > item.MainHeight);
            (int outputWidth, int outputHeight) = ResolveOutputSize(request.Quality, landscape);

            EnsureFreeSpace(
                outputDirectory,
                plannedDuration.TotalSeconds,
                request.Quality,
                request.IncludeAudio,
                warnings);

            for (int index = 0; index < prepared.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PreparedItem item = prepared[index];
                if (item.OverlayPath is null)
                {
                    continue;
                }

                Report(
                    StoryExportPhase.Preparing,
                    19 + (1.0 * index / prepared.Count),
                    item.OverlayPath,
                    item.Number,
                    $"Préparation du calque de l'élément {item.Number} sur {prepared.Count}.");

                Rect mainRect = UniformRect(
                    item.MainWidth,
                    item.MainHeight,
                    outputWidth,
                    outputHeight);
                Rect overlayRect = FitInside(
                    item.OverlayWidth,
                    item.OverlayHeight,
                    mainRect);

                MediaClip overlayClip = await CreateImageClipAsync(
                        item.OverlayPath,
                        item.Duration,
                        scratch,
                        warnings,
                        $"calque de l'élément {item.Number}",
                        cancellationToken)
                    .ConfigureAwait(false);
                overlayClip.Volume = 0.0;

                var overlay = new MediaOverlay(overlayClip)
                {
                    Delay = item.Start,
                    Opacity = 1.0,
                    AudioEnabled = false,
                    Position = overlayRect,
                };

                var layer = new MediaOverlayLayer();
                layer.Overlays.Add(overlay);
                composition.OverlayLayers.Add(layer);
            }

            bool includeAudioTrack = request.IncludeAudio &&
                                     prepared.Any(item => HasEmbeddedAudio(item.Clip));
            string? firstOverlayPath = prepared
                .FirstOrDefault(item => item.OverlayPath is not null)
                ?.OverlayPath;
            MediaEncodingProfile profile = CreateEncodingProfile(
                request.Quality,
                outputWidth,
                outputHeight,
                includeAudioTrack);

            temporaryMp4Path = BuildTemporaryPath(outputDirectory);

            StorageFolder outputFolder;
            StorageFile temporaryFile;
            try
            {
                outputFolder = await StorageFolder.GetFolderFromPathAsync(outputDirectory)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                temporaryFile = await outputFolder
                    .CreateFileAsync(Path.GetFileName(temporaryMp4Path), CreationCollisionOption.ReplaceExisting)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new StoryExportException(
                    $"Le fichier temporaire « {temporaryMp4Path} » n'a pas pu être créé dans " +
                    $"« {outputDirectory} » : {Describe(ex)}",
                    ex);
            }

            Report(
                StoryExportPhase.Rendering,
                20,
                prepared[0].MainPath,
                1,
                $"Rendu Media Foundation en {outputWidth}×{outputHeight} (H.264/AAC).");

            var renderProgress = new Progress<double>(value =>
            {
                double normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
                PreparedItem current = FindItemAt(prepared, normalized / 100.0);
                Report(
                    StoryExportPhase.Rendering,
                    20 + (normalized * 0.75),
                    current.MainPath,
                    current.Number,
                    $"Rendu Media Foundation : {normalized.ToString("0", CultureInfo.InvariantCulture)} %");
            });

            TranscodeFailureReason reason;
            try
            {
                var operation = composition.RenderToFileAsync(
                    temporaryFile,
                    MediaTrimmingPreference.Precise,
                    profile);
                reason = await operation
                    .AsTask(cancellationToken, renderProgress)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new StoryExportException(
                    "Le rendu Media Foundation a échoué : " + Describe(ex) + ". " +
                    CodecHint() + OverlayRenderHint(firstOverlayPath),
                    ex);
            }

            if (reason != TranscodeFailureReason.None)
            {
                throw new StoryExportException(
                    MapTranscodeFailure(reason, outputWidth, outputHeight, firstOverlayPath));
            }

            // Une annulation demandée juste après la fin du rendu ne doit pas produire un
            // fichier final alors que l'utilisateur a annulé.
            cancellationToken.ThrowIfCancellationRequested();

            var temporaryInfo = new FileInfo(temporaryMp4Path);
            if (!temporaryInfo.Exists || temporaryInfo.Length <= 0)
            {
                throw new StoryExportException(
                    "Media Foundation a signalé un rendu réussi, mais le fichier temporaire " +
                    $"« {temporaryMp4Path} » est absent ou vide. Aucun fichier final n'a été produit.");
            }

            Report(StoryExportPhase.Finalizing, 96, null, 0, "Vérification du fichier produit.");
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Même dossier, même volume : le renommage est atomique. Sans autorisation
                // explicite, une destination apparue entre-temps fait échouer l'opération.
                File.Move(temporaryMp4Path, outputFullPath!, request.OverwriteExistingFile);
                temporaryMp4Path = null;
            }
            catch (IOException ex)
            {
                throw new StoryExportException(
                    request.OverwriteExistingFile
                        ? $"Le fichier final « {outputFullPath} » n'a pas pu être remplacé : {Describe(ex)}"
                        : $"Le fichier « {outputFullPath} » existe déjà ou est verrouillé ; " +
                          "l'export n'a pas écrasé la destination. Choisissez un autre chemin ou " +
                          "activez OverwriteExistingFile.",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new StoryExportException(
                    $"Le fichier final « {outputFullPath} » n'a pas pu être écrit : {Describe(ex)}",
                    ex);
            }

            var finalInfo = new FileInfo(outputFullPath!);
            if (!finalInfo.Exists || finalInfo.Length <= 0)
            {
                TryDelete(outputFullPath!);
                throw new StoryExportException(
                    $"Le fichier final « {outputFullPath} » est absent ou vide après le renommage ; " +
                    "il a été supprimé plutôt que présenté comme un export réussi.");
            }

            TimeSpan actualDuration = plannedDuration;
            try
            {
                MediaInfo produced = MediaInspector.InspectFile(outputFullPath!);
                if (produced.DurationMs is > 0)
                {
                    actualDuration = TimeSpan.FromMilliseconds(produced.DurationMs.Value);
                }
            }
            catch (Exception)
            {
                // La durée du conteneur est indicative : le fichier et sa taille ont déjà été vérifiés.
            }

            Report(StoryExportPhase.Completed, 100, null, 0, "Export terminé.");
            return StoryExportResult.Success(
                outputFullPath!,
                actualDuration,
                finalInfo.Length,
                warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Report(StoryExportPhase.Cancelled, 0, null, 0, "Export annulé.");
            return StoryExportResult.Failure(
                "L'export a été annulé ; aucun fichier final n'a été produit.",
                warnings,
                plannedDuration,
                cancelled: true);
        }
        catch (StoryExportException ex)
        {
            Report(StoryExportPhase.Failed, 0, null, 0, ex.Message);
            return StoryExportResult.Failure(ex.Message, warnings, plannedDuration);
        }
        catch (UnauthorizedAccessException ex)
        {
            string message =
                $"L'export a échoué : accès refusé sur « {outputFullPath ?? request.OutputPath} » " +
                $"ou sur un média source. {Describe(ex)}";
            Report(StoryExportPhase.Failed, 0, null, 0, message);
            return StoryExportResult.Failure(message, warnings, plannedDuration);
        }
        catch (COMException ex)
        {
            string message =
                "L'export a échoué : Media Foundation a renvoyé une erreur " +
                $"(HRESULT 0x{ex.HResult:X8}) : {ex.Message}. " +
                CodecHint();
            Report(StoryExportPhase.Failed, 0, null, 0, message);
            return StoryExportResult.Failure(message, warnings, plannedDuration);
        }
        catch (OperationCanceledException ex)
        {
            string message =
                $"L'export a échoué : une opération a été annulée alors que l'export était encore " +
                $"demandé ({Describe(ex)}). Aucun fichier final n'a été produit.";
            Report(StoryExportPhase.Failed, 0, null, 0, message);
            return StoryExportResult.Failure(message, warnings, plannedDuration);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string message = $"L'export a échoué : {Describe(ex)}";
            Report(StoryExportPhase.Failed, 0, null, 0, message);
            return StoryExportResult.Failure(message, warnings, plannedDuration);
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryMp4Path))
            {
                TryDelete(temporaryMp4Path);
            }

            scratch.Dispose();
        }
    }

    private static string? Validate(
        StoryExportRequest request,
        out string? outputFullPath,
        out int itemCount)
    {
        outputFullPath = null;
        itemCount = 0;

        if (request.Items is null || request.Items.Count == 0)
        {
            return "Aucun média à exporter : la Story est vide.";
        }

        itemCount = request.Items.Count;
        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            return "Le chemin du fichier de sortie est vide.";
        }

        if (!double.IsFinite(request.PhotoDurationSeconds) ||
            request.PhotoDurationSeconds < 0.1 ||
            request.PhotoDurationSeconds > 600)
        {
            return "La durée d'affichage des photos doit être comprise entre 0,1 et 600 secondes.";
        }

        if (request.Quality is not (StoryExportQuality.Hd1080 or StoryExportQuality.Hd720))
        {
            return $"La qualité d'export « {request.Quality} » n'est pas prise en charge.";
        }

        try
        {
            outputFullPath = Path.GetFullPath(request.OutputPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Le chemin de sortie « {request.OutputPath} » est invalide : {Describe(ex)}";
        }

        if (!string.Equals(Path.GetExtension(outputFullPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return $"Le fichier de sortie « {outputFullPath} » doit porter l'extension .mp4 " +
                   "(conteneur produit par Media Foundation).";
        }

        if (Directory.Exists(outputFullPath))
        {
            return $"Le chemin de sortie « {outputFullPath} » désigne un dossier, pas un fichier.";
        }

        if (File.Exists(outputFullPath) && !request.OverwriteExistingFile)
        {
            return $"Le fichier « {outputFullPath} » existe déjà. Choisissez un autre chemin ou " +
                   "activez OverwriteExistingFile pour le remplacer explicitement.";
        }

        // Les doublons sont autorisés : une Story peut volontairement montrer deux fois le même
        // souvenir. Chaque source reste néanmoins vérifiée et ne peut jamais être la sortie.
        for (int index = 0; index < request.Items.Count; index++)
        {
            StoryExportItem? item = request.Items[index];
            int number = index + 1;
            if (item is null)
            {
                return $"L'élément {number} de la Story est nul.";
            }

            if (string.IsNullOrWhiteSpace(item.MainPath))
            {
                return $"L'élément {number} n'a pas de chemin de média principal.";
            }

            if (item.MediaKind is not (MediaKind.Photo or MediaKind.Video))
            {
                return $"L'élément {number} a un type de média inconnu ({item.MediaKind}) ; " +
                       "seules les photos et les vidéos peuvent être exportées.";
            }

            string mainPath;
            string? overlayPath = null;
            try
            {
                mainPath = Path.GetFullPath(item.MainPath.Trim());
                if (!string.IsNullOrWhiteSpace(item.OverlayPath))
                {
                    overlayPath = Path.GetFullPath(item.OverlayPath.Trim());
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return $"Un chemin de l'élément {number} est invalide : {Describe(ex)}";
            }

            if (!File.Exists(mainPath))
            {
                return $"L'élément {number} est introuvable : le média « {mainPath} » n'existe pas.";
            }

            if (string.Equals(mainPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return $"L'élément {number} utilise « {mainPath} » comme source et comme sortie. " +
                       "Une source n'est jamais réécrite ; choisissez un autre fichier de sortie.";
            }

            if (overlayPath is not null)
            {
                if (!File.Exists(overlayPath))
                {
                    return $"L'élément {number} référence un calque introuvable : « {overlayPath} ».";
                }

                if (string.Equals(overlayPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return $"L'élément {number} utilise « {overlayPath} » comme calque et comme sortie. " +
                           "Une source n'est jamais réécrite ; choisissez un autre fichier de sortie.";
                }
            }
        }

        return null;
    }

    private static double EstimateDurationSeconds(StoryExportRequest request)
    {
        double total = 0;
        foreach (StoryExportItem item in request.Items)
        {
            if (item.MediaKind == MediaKind.Photo)
            {
                total += request.PhotoDurationSeconds;
            }
            else if (double.IsFinite(item.DurationSeconds) && item.DurationSeconds > 0)
            {
                total += Math.Min(item.DurationSeconds, 24 * 60 * 60);
            }
            else
            {
                // Estimation prudente : la vérification exacte aura lieu après ouverture des clips.
                total += 30;
            }
        }

        return Math.Max(total, 1);
    }

    private static void EnsureFreeSpace(
        string outputDirectory,
        double seconds,
        StoryExportQuality quality,
        bool includeAudio,
        ICollection<string> warnings)
    {
        long videoBitrate = quality == StoryExportQuality.Hd720 ? 5_000_000 : 8_000_000;
        long audioBitrate = includeAudio ? AudioBitrate : 0;
        long required = (long)(
            Math.Max(seconds, 1) *
            (videoBitrate + audioBitrate) / 8.0 *
            1.25) + DiskSafetyMarginBytes;

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(outputDirectory));
            if (string.IsNullOrEmpty(root))
            {
                warnings.Add(
                    "L'espace disque disponible n'a pas pu être vérifié : la racine du volume " +
                    "de sortie est inconnue.");
                return;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                warnings.Add(
                    $"L'espace disque disponible n'a pas pu être vérifié : le volume « {root} » " +
                    "n'est pas prêt.");
                return;
            }

            if (drive.AvailableFreeSpace < required)
            {
                throw new StoryExportException(
                    $"L'export a été refusé : environ {FormatBytes(required)} sont nécessaires, " +
                    $"mais le volume « {root} » ne dispose que de " +
                    $"{FormatBytes(drive.AvailableFreeSpace)}. Libérez de l'espace ou choisissez " +
                    "un autre dossier de sortie.");
            }
        }
        catch (StoryExportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            warnings.Add(
                "L'espace disque disponible n'a pas pu être vérifié avant l'export : " +
                Describe(ex));
        }
    }

    private static async Task<MediaClip> CreateVideoClipAsync(
        string path,
        ICollection<string> warnings,
        string label,
        CancellationToken cancellationToken)
    {
        StorageFile file = await GetStorageFileAsync(path, label, cancellationToken).ConfigureAwait(false);

        MediaClip clip;
        try
        {
            clip = await MediaClip.CreateFromFileAsync(file)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new StoryExportException(
                $"Media Foundation n'a pas pu ouvrir la vidéo « {path} » ({label}) : {Describe(ex)}. " +
                "Le codec source n'est peut-être pas pris en charge par cette installation Windows " +
                "(HEVC, AV1 ou VP9 sans extension) ; installez le codec correspondant ou convertissez " +
                "la vidéo en H.264.",
                ex);
        }

        string? subtype = TryGetVideoSubtype(clip);
        if (IsHevc(subtype))
        {
            warnings.Add(
                $"La vidéo « {path} » ({label}) est encodée en HEVC ({subtype}). Le rendu dépend du " +
                "décodeur HEVC installé (extension « HEVC Video Extensions ») ; s'il manque, " +
                "l'export échouera proprement avec un diagnostic explicite.");
        }

        VideoEncodingProperties? videoProperties;
        try
        {
            videoProperties = clip.GetVideoEncodingProperties();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new StoryExportException(
                $"La vidéo « {path} » ({label}) n'a pas pu exposer ses propriétés vidéo : " +
                Describe(ex),
                ex);
        }

        if (videoProperties is null || videoProperties.Width == 0 || videoProperties.Height == 0)
        {
            throw new StoryExportException(
                $"La vidéo « {path} » ({label}) ne contient aucune piste vidéo exploitable pour " +
                "Media Foundation.");
        }

        return clip;
    }

    private static async Task<MediaClip> CreateImageClipAsync(
        string path,
        TimeSpan duration,
        ScratchSpace scratch,
        ICollection<string> warnings,
        string label,
        CancellationToken cancellationToken)
    {
        StorageFile file = await GetStorageFileAsync(path, label, cancellationToken).ConfigureAwait(false);

        try
        {
            return await MediaClip.CreateFromImageFileAsync(file, duration)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception first) when (first is not OutOfMemoryException)
        {
            // Media Foundation ne décode pas WebP nativement sur toutes les installations.
            // On passe alors par SkiaSharp, déjà utilisé par Memo, vers un PNG temporaire.
            string convertedPath;
            try
            {
                convertedPath = ConvertImageToPng(path, scratch.GetDirectory());
            }
            catch (Exception conversion) when (conversion is not OutOfMemoryException)
            {
                throw new StoryExportException(
                    $"L'image « {path} » ({label}) n'a pas pu être lue par Media Foundation " +
                    $"({Describe(first)}), et sa conversion de secours en PNG a échoué " +
                    $"({Describe(conversion)}).",
                    conversion);
            }

            warnings.Add(
                $"L'image « {path} » ({label}) a été convertie en PNG temporaire, car Media " +
                $"Foundation ne lisait pas directement son format ({Describe(first)}).");

            StorageFile convertedFile = await GetStorageFileAsync(
                    convertedPath,
                    label,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                return await MediaClip.CreateFromImageFileAsync(convertedFile, duration)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception second) when (second is not OutOfMemoryException)
            {
                throw new StoryExportException(
                    $"L'image « {path} » ({label}) n'a pas pu être préparée pour l'export, même " +
                    $"après conversion PNG temporaire : {Describe(second)}",
                    second);
            }
        }
    }

    private static async Task<StorageFile> GetStorageFileAsync(
        string path,
        string label,
        CancellationToken cancellationToken)
    {
        try
        {
            return await StorageFile.GetFileFromPathAsync(path)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new StoryExportException(
                $"Le fichier « {path} » ({label}) n'a pas pu être ouvert par Media Foundation : " +
                Describe(ex),
                ex);
        }
    }

    private static string ConvertImageToPng(string sourcePath, string scratchDirectory)
    {
        using DecodedImage decoded = ImageDecoder.Decode(sourcePath);
        using SKData? encoded = decoded.Bitmap.Encode(SKEncodedImageFormat.Png, 100);
        if (encoded is null || encoded.Size == 0)
        {
            throw new InvalidOperationException(
                $"L'encodage PNG de secours de « {sourcePath} » a produit un fichier vide.");
        }

        string target = Path.Combine(
            scratchDirectory,
            $"image-{Guid.NewGuid():N}.png");

        using (var stream = new FileStream(
                   target,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 81920,
                   FileOptions.SequentialScan))
        {
            encoded.SaveTo(stream);
            stream.Flush(flushToDisk: true);
        }

        return target;
    }

    private static (int Width, int Height) ResolveMainDimensions(
        StoryExportItem item,
        string mainPath,
        MediaClip clip)
    {
        if (item.Width > 0 && item.Height > 0)
        {
            return (item.Width, item.Height);
        }

        if (TryInspectDisplaySize(mainPath, out int width, out int height))
        {
            return (width, height);
        }

        try
        {
            VideoEncodingProperties? properties = clip.GetVideoEncodingProperties();
            if (properties is { Width: > 0, Height: > 0 })
            {
                return ((int)properties.Width, (int)properties.Height);
            }
        }
        catch (Exception)
        {
            // Dimensions indisponibles : seule la présence d'un calque rendra ce cas bloquant.
        }

        return (0, 0);
    }

    private static (int Width, int Height) ResolveOverlayDimensions(string overlayPath, int number)
    {
        if (TryInspectDisplaySize(overlayPath, out int width, out int height))
        {
            return (width, height);
        }

        throw new StoryExportException(
            $"Les dimensions d'affichage du calque de l'élément {number} " +
            $"« {overlayPath} » sont inconnues ; le calque ne peut pas être positionné sans " +
            "risquer de le déformer.");
    }

    private static bool TryInspectDisplaySize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            MediaInfo info = MediaInspector.InspectFile(path);
            DisplayDimensions size = DisplayGeometry.GetDisplaySize(info);
            if (!size.IsEmpty)
            {
                width = size.Width;
                height = size.Height;
                return true;
            }
        }
        catch (Exception)
        {
            // Un en-tête illisible sera signalé plus loin par Media Foundation, avec le chemin.
        }

        return false;
    }

    private static Rect UniformRect(
        int contentWidth,
        int contentHeight,
        int targetWidth,
        int targetHeight)
    {
        double scale = Math.Min(
            targetWidth / (double)contentWidth,
            targetHeight / (double)contentHeight);
        double width = contentWidth * scale;
        double height = contentHeight * scale;
        return new Rect(
            (targetWidth - width) / 2.0,
            (targetHeight - height) / 2.0,
            width,
            height);
    }

    private static Rect FitInside(int width, int height, Rect area)
    {
        double scale = Math.Min(area.Width / width, area.Height / height);
        double fittedWidth = width * scale;
        double fittedHeight = height * scale;

        // Le calque Snapchat partage normalement les dimensions du média : on conserve alors
        // exactement le rectangle d'affichage, sans arrondi subpixel visible.
        if (Math.Abs(fittedWidth - area.Width) < 0.5 &&
            Math.Abs(fittedHeight - area.Height) < 0.5)
        {
            return area;
        }

        return new Rect(
            area.X + ((area.Width - fittedWidth) / 2.0),
            area.Y + ((area.Height - fittedHeight) / 2.0),
            fittedWidth,
            fittedHeight);
    }

    private static (int Width, int Height) ResolveOutputSize(
        StoryExportQuality quality,
        bool landscape)
    {
        int shortSide = quality == StoryExportQuality.Hd720 ? 720 : 1080;
        int longSide = shortSide * 16 / 9;
        return landscape ? (longSide, shortSide) : (shortSide, longSide);
    }

    private static MediaEncodingProfile CreateEncodingProfile(
        StoryExportQuality quality,
        int width,
        int height,
        bool includeAudioTrack)
    {
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.Bitrate = quality == StoryExportQuality.Hd720 ? 5_000_000u : 8_000_000u;
        profile.Video.FrameRate.Numerator = VideoFrameRate;
        profile.Video.FrameRate.Denominator = 1;

        if (includeAudioTrack)
        {
            profile.Audio = AudioEncodingProperties.CreateAac(48_000, 2, AudioBitrate);
        }
        else
        {
            // Aucune piste audio dans le MP4 : soit l'utilisateur a demandé le silence, soit
            // aucun clip n'apporte de son. Les clips vidéo sont en plus mis à Volume=0 quand
            // IncludeAudio=false, ce qui protège aussi le cas où Media Foundation ignorerait
            // ce profil sans audio.
            profile.Audio = null;
        }

        return profile;
    }

    private static PreparedItem FindItemAt(
        IReadOnlyList<PreparedItem> items,
        double fraction)
    {
        double position = Math.Clamp(fraction, 0, 1);
        TimeSpan target = items[^1].Start + items[^1].Duration;
        target = TimeSpan.FromTicks((long)(target.Ticks * position));

        foreach (PreparedItem item in items)
        {
            if (target < item.Start + item.Duration)
            {
                return item;
            }
        }

        return items[^1];
    }

    private static string MapTranscodeFailure(
        TranscodeFailureReason reason,
        int width,
        int height,
        string? firstOverlayPath) => reason switch
    {
        TranscodeFailureReason.CodecNotFound =>
            $"Le rendu a échoué : aucun codec compatible n'a été trouvé pour produire du H.264/AAC " +
            $"en {width}×{height}. Media Foundation ou l'encodeur H.264 de Windows est peut-être " +
            "absent (Windows N, installation réduite). " + CodecHint(),

        TranscodeFailureReason.InvalidProfile =>
            $"Le rendu a échoué : Media Foundation a refusé le profil {width}×{height} H.264/AAC. " +
            "La résolution ou le profil d'encodage n'est pas accepté par cette installation. " +
            CodecHint(),

        _ =>
            $"Le rendu a échoué : Media Foundation a signalé « {reason} » sans préciser la cause. " +
            "Consultez le fichier source et l'installation des codecs Windows. " +
            CodecHint() + OverlayRenderHint(firstOverlayPath),
    };

    private static string CodecHint() =>
        "L'export nécessite un encodeur H.264 et un décodeur pour chaque source ; le HEVC en " +
        "entrée n'est pas garanti sans « HEVC Video Extensions ». Aucun fichier final n'a été produit.";

    private static string OverlayRenderHint(string? firstOverlayPath) =>
        firstOverlayPath is null
            ? string.Empty
            : $" Un calque est inclus (« {firstOverlayPath} ») ; vérifiez que ce PNG est " +
              "lisible et accepté par Media Foundation.";

    private static bool HasEmbeddedAudio(MediaClip clip)
    {
        try
        {
            return clip.EmbeddedAudioTracks.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? TryGetVideoSubtype(MediaClip clip)
    {
        try
        {
            return clip.GetVideoEncodingProperties()?.Subtype;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsHevc(string? subtype)
    {
        if (string.IsNullOrWhiteSpace(subtype))
        {
            return false;
        }

        string value = subtype.ToUpperInvariant();
        return value.Contains("HEVC", StringComparison.Ordinal) ||
               value.Contains("H265", StringComparison.Ordinal) ||
               value.Contains("HVC1", StringComparison.Ordinal) ||
               value.Contains("HEV1", StringComparison.Ordinal);
    }

    private static string BuildTemporaryPath(string outputDirectory) =>
        Path.Combine(
            outputDirectory,
            $".memo-export-{Guid.NewGuid():N}.tmp.mp4");

    private static void TryDelete(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static string Describe(Exception exception)
    {
        Exception root = exception.GetBaseException();
        if (root is COMException com)
        {
            return $"{root.Message} (HRESULT 0x{com.HResult:X8})";
        }

        return string.IsNullOrWhiteSpace(root.Message) ? root.GetType().Name : root.Message;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " Go";
        }

        return Math.Max(1, bytes / (1024 * 1024)) + " Mo";
    }

    /// <summary>Élément dont le clip a été créé et dont la position dans la composition est connue.</summary>
    private sealed class PreparedItem
    {
        public PreparedItem(
            int number,
            MediaClip clip,
            TimeSpan start,
            TimeSpan duration,
            string mainPath,
            string? overlayPath,
            int mainWidth,
            int mainHeight,
            int overlayWidth,
            int overlayHeight)
        {
            Number = number;
            Clip = clip;
            Start = start;
            Duration = duration;
            MainPath = mainPath;
            OverlayPath = overlayPath;
            MainWidth = mainWidth;
            MainHeight = mainHeight;
            OverlayWidth = overlayWidth;
            OverlayHeight = overlayHeight;
        }

        public int Number { get; }
        public MediaClip Clip { get; }
        public TimeSpan Start { get; }
        public TimeSpan Duration { get; }
        public string MainPath { get; }
        public string? OverlayPath { get; }
        public int MainWidth { get; }
        public int MainHeight { get; }
        public int OverlayWidth { get; }
        public int OverlayHeight { get; }
    }

    /// <summary>Dossier temporaire créé uniquement si une conversion d'image de secours est nécessaire.</summary>
    private sealed class ScratchSpace : IDisposable
    {
        private string? _directory;

        public string GetDirectory()
        {
            if (_directory is not null)
            {
                return _directory;
            }

            string root = Path.Combine(Path.GetTempPath(), "Memo", "export");
            Directory.CreateDirectory(root);
            _directory = Directory.CreateDirectory(
                Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
            return _directory;
        }

        public void Dispose()
        {
            if (_directory is null)
            {
                return;
            }

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Media Foundation peut conserver un handle quelques instants ; le dossier est
                // sous %TEMP% et sera nettoyé par le système.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Erreur attendue, déjà formulée pour l'utilisateur.</summary>
    private sealed class StoryExportException : Exception
    {
        public StoryExportException(string message)
            : base(message)
        {
        }

        public StoryExportException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
