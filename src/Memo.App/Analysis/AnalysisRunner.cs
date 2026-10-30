using System.Diagnostics;
using Memo.Core;

namespace Memo.App.Analysis;

/// <summary>Étape d'une passe d'analyse, utilisée par la barre de progression de l'interface.</summary>
public enum AnalysisPhase
{
    Preparing,
    Ocr,
    VisualHash,
    Faces,
    Done,
    Cancelled,
    Failed,
}

/// <summary>Progression d'une passe : phase, éléments faits, total et libellé courant.</summary>
public sealed record AnalysisProgress(AnalysisPhase Phase, int Done, int Total, string Message);

/// <summary>
/// Options d'une passe d'analyse. Toutes les analyses sont facultatives et locales ; l'appelant
/// décide de les activer, par exemple à partir de <c>AppSettings.OcrEnabled</c> et
/// <c>AppSettings.FacesEnabled</c>.
/// </summary>
public sealed record AnalysisRunOptions
{
    /// <summary>Texte des photos (Windows.Media.Ocr), écrit dans <c>analysis_ocr</c>.</summary>
    public bool Ocr { get; init; } = true;

    /// <summary>Empreinte perceptuelle des photos, écrite dans <c>analysis_visual</c>.</summary>
    public bool VisualHash { get; init; } = true;

    /// <summary>Détection et regroupement des visages. Plus coûteux, désactivé par défaut.</summary>
    public bool Faces { get; init; }

    /// <summary>
    /// Ignore les souvenirs qui ont déjà un texte OCR ou une empreinte visuelle. Activé par
    /// défaut : une passe conserve donc les résultats existants, et le recalcul complet doit être
    /// demandé explicitement (bouton « Tout réanalyser » dans Réglages).
    /// </summary>
    public bool SkipExisting { get; init; } = true;

    /// <summary>Taille des pages lues dans le catalogue (bornée entre 50 et 5 000).</summary>
    public int PageSize { get; init; } = 500;

    /// <summary>
    /// Active toutes les analyses, y compris les visages. La passe reste incrémentale :
    /// <see cref="SkipExisting"/> conserve sa valeur par défaut.
    /// </summary>
    public static AnalysisRunOptions All => new() { Ocr = true, VisualHash = true, Faces = true };

    /// <summary>N'active rien : utile pour construire une variante avec <c>with</c>.</summary>
    public static AnalysisRunOptions None => new() { Ocr = false, VisualHash = false, Faces = false };
}

/// <summary>Bilan d'une passe d'analyse sur une bibliothèque.</summary>
public sealed record AnalysisSummary(
    int MemoriesSeen,
    int PhotosEligible,
    int ExcludedSkipped,
    int VideosSkipped,
    int OcrCompleted,
    int OcrFailed,
    int OcrSkipped,
    int VisualHashesCompleted,
    int VisualHashesFailed,
    int VisualHashesSkipped,
    int FacesDetected,
    int FaceGroupsCreated,
    TimeSpan Duration,
    bool Cancelled,
    string? Error,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Orchestre les analyses locales d'une bibliothèque : parcourt les souvenirs qui ont un média
/// principal photo, ignore les cartes exclues, applique l'OCR, l'empreinte visuelle et/ou les
/// visages selon les options, puis écrit les résultats dans <see cref="MemoLibrary.Analysis"/>.
///
/// Le décodage des images est délégué au pool de threads (SkiaSharp) ; l'appelant peut donc lancer
/// la passe depuis l'interface sans bloquer la fenêtre. Une erreur sur un fichier devient un
/// diagnostic et n'interrompt pas les suivants. Une annulation retourne un bilan
/// <see cref="AnalysisSummary.Cancelled"/> plutôt qu'une exception.
///
/// Les vidéos ne sont pas analysées : leur média principal n'est pas une image. Elles sont
/// comptées dans <see cref="AnalysisSummary.VideosSkipped"/> pour que le bilan reste honnête.
/// </summary>
public sealed class AnalysisRunner
{
    private const int MaxDiagnostics = 50;

    private readonly MemoLibrary _library;
    private readonly LocalOcrService _ocr;
    private readonly FaceAnalysisService _faces;

    public AnalysisRunner(
        MemoLibrary library,
        LocalOcrService? ocr = null,
        FaceAnalysisService? faces = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _ocr = ocr ?? new LocalOcrService();
        _faces = faces ?? new FaceAnalysisService();
    }

    /// <summary>
    /// Exécute la passe demandée. La méthode ne lève pas d'exception pour un média illisible, un
    /// pack OCR absent ou un détecteur de visages indisponible : ces cas sont des diagnostics.
    /// Seule une annulation via <paramref name="ct"/> arrête la passe, et elle est convertie en
    /// bilan annulé.
    /// </summary>
    public async Task<AnalysisSummary> RunAsync(
        AnalysisRunOptions? options = null,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new AnalysisRunOptions();
        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<string>();

        int memoriesSeen = 0;
        int photosEligible = 0;
        int excludedSkipped = 0;
        int videosSkipped = 0;
        int ocrCompleted = 0;
        int ocrFailed = 0;
        int ocrSkipped = 0;
        int hashesCompleted = 0;
        int hashesFailed = 0;
        int hashesSkipped = 0;
        int facesDetected = 0;
        int faceGroupsCreated = 0;
        bool cancelled = false;
        string? fatalError = null;

        try
        {
            progress?.Report(new AnalysisProgress(
                AnalysisPhase.Preparing, 0, 0, "Lecture de la bibliothèque…"));

            var cards = await LoadCardsAsync(options.PageSize, ct).ConfigureAwait(false);
            memoriesSeen = cards.Count;
            var photos = new List<MemoryCard>();
            foreach (var card in cards)
            {
                if (card.IsExcluded)
                {
                    excludedSkipped++;
                    continue;
                }

                if (card.MainAssetId is null)
                {
                    // Calque seul : il n'y a pas d'image principale à analyser.
                    continue;
                }

                if (card.Kind == MediaKind.Video)
                {
                    videosSkipped++;
                    continue;
                }

                if (card.Kind == MediaKind.Photo)
                {
                    photos.Add(card);
                }
            }

            photosEligible = photos.Count;
            progress?.Report(new AnalysisProgress(
                AnalysisPhase.Preparing, 0, photos.Count,
                $"{photos.Count} photo(s) à analyser, {excludedSkipped} exclue(s), {videosSkipped} vidéo(s) ignorée(s)."));

            if (options.Ocr && photos.Count > 0)
            {
                var pass = await RunOcrPassAsync(
                    photos, options.SkipExisting, progress, diagnostics, ct).ConfigureAwait(false);
                ocrCompleted = pass.Completed;
                ocrFailed = pass.Failed;
                ocrSkipped = pass.Skipped;
            }

            if (options.VisualHash && photos.Count > 0)
            {
                var pass = await RunVisualHashPassAsync(
                    photos, options.SkipExisting, progress, diagnostics, ct).ConfigureAwait(false);
                hashesCompleted = pass.Completed;
                hashesFailed = pass.Failed;
                hashesSkipped = pass.Skipped;
            }

            if (options.Faces && photos.Count > 0)
            {
                var pass = await RunFacePassAsync(photos, progress, diagnostics, ct).ConfigureAwait(false);
                if (pass is not null)
                {
                    facesDetected = pass.FacesDetected;
                    faceGroupsCreated = pass.GroupsCreated;
                    if (pass.Cancelled)
                    {
                        cancelled = true;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            AddDiagnostic(diagnostics, "Analyse annulée.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fatalError = $"L'analyse a échoué : {ex.Message}";
            AddDiagnostic(diagnostics, fatalError);
        }

        stopwatch.Stop();
        progress?.Report(new AnalysisProgress(
            cancelled
                ? AnalysisPhase.Cancelled
                : fatalError is null
                    ? AnalysisPhase.Done
                    : AnalysisPhase.Failed,
            cancelled || fatalError is not null ? 0 : photosEligible,
            photosEligible,
            cancelled
                ? "Analyse annulée."
                : fatalError ?? $"Analyse terminée : {ocrCompleted} texte(s), {hashesCompleted} empreinte(s), {facesDetected} visage(s)."));

        return new AnalysisSummary(
            memoriesSeen,
            photosEligible,
            excludedSkipped,
            videosSkipped,
            ocrCompleted,
            ocrFailed,
            ocrSkipped,
            hashesCompleted,
            hashesFailed,
            hashesSkipped,
            facesDetected,
            faceGroupsCreated,
            stopwatch.Elapsed,
            cancelled,
            fatalError,
            diagnostics.ToArray());
    }

    private async Task<List<MemoryCard>> LoadCardsAsync(int pageSize, CancellationToken ct)
    {
        int size = Math.Clamp(pageSize, 50, 5000);
        var cards = new List<MemoryCard>();
        int offset = 0;
        while (true)
        {
            var page = await _library.Catalog.QueryAsync(
                new MemoryQuery
                {
                    IncludeExcluded = true,
                    Descending = false,
                    Offset = offset,
                    Limit = size,
                },
                ct).ConfigureAwait(false);

            cards.AddRange(page);
            if (page.Count < size)
            {
                break;
            }

            offset += page.Count;
        }

        return cards;
    }

    private async Task<(int Completed, int Failed, int Skipped)> RunOcrPassAsync(
        IReadOnlyList<MemoryCard> photos,
        bool skipExisting,
        IProgress<AnalysisProgress>? progress,
        List<string> diagnostics,
        CancellationToken ct)
    {
        int completed = 0;
        int failed = 0;
        int skipped = 0;

        for (int index = 0; index < photos.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var card = photos[index];

            // GetAssetPath/GetThumbnailPath ouvrent une connexion SQLite synchrone : on les sort du
            // fil d'interface, comme le décodage.
            string? path = await Task.Run(
                () => ResolveImagePath(card, preferThumbnail: false), ct).ConfigureAwait(false);
            string label = path is null ? $"souvenir {card.Id}" : DisplayName(path);
            progress?.Report(new AnalysisProgress(
                AnalysisPhase.Ocr, index, photos.Count,
                $"OCR {index + 1}/{photos.Count} — {label}"));

            if (path is null)
            {
                failed++;
                AddDiagnostic(diagnostics, $"Souvenir {card.Id} : aucun fichier image lisible pour l'OCR.");
                continue;
            }

            if (skipExisting)
            {
                try
                {
                    if (await _library.Analysis.GetOcrTextAsync(card.Id, ct).ConfigureAwait(false) is not null)
                    {
                        skipped++;
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AddDiagnostic(diagnostics, $"Lecture OCR existante impossible pour le souvenir {card.Id} : {ex.Message}");
                }
            }

            var result = await _ocr.RecognizeAsync(path, ct).ConfigureAwait(false);
            if (result.Cancelled)
            {
                throw new OperationCanceledException(ct);
            }

            if (result.EngineUnavailable)
            {
                // Inutile de répéter le même échec sur chaque photo : on arrête la passe OCR et on
                // laisse les empreintes/visages continuer s'ils sont activés.
                AddDiagnostic(diagnostics, result.Error ?? LocalOcrService.MissingLanguagePackMessage);
                break;
            }

            if (!result.Succeeded)
            {
                failed++;
                AddDiagnostic(diagnostics, result.Error ?? $"OCR impossible pour « {path} ».");
                continue;
            }

            try
            {
                string engine = string.IsNullOrWhiteSpace(result.Language)
                    ? LocalOcrService.EngineId
                    : $"{LocalOcrService.EngineId}/{result.Language}";

                // Un calque porte souvent le texte le plus lisible (heure, légende,
                // sticker) : on l'indexe avec le média principal.
                var combined = result.Text;
                if (card.OverlayAssetId is long overlayAssetId)
                {
                    try
                    {
                        var overlayPath = await Task.Run(
                            () => _library.Catalog.GetAssetPath(overlayAssetId), ct).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(overlayPath) && File.Exists(overlayPath))
                        {
                            var overlayResult = await _ocr.RecognizeAsync(overlayPath, ct).ConfigureAwait(false);
                            if (overlayResult.Succeeded && !string.IsNullOrWhiteSpace(overlayResult.Text))
                            {
                                combined = string.IsNullOrWhiteSpace(combined)
                                    ? overlayResult.Text
                                    : combined + "\n" + overlayResult.Text;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        AddDiagnostic(diagnostics, $"Calque OCR ignoré pour le souvenir {card.Id} : {ex.Message}");
                    }
                }

                await _library.Analysis.SetOcrTextAsync(card.Id, combined, engine, ct).ConfigureAwait(false);
                completed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                AddDiagnostic(diagnostics, $"Texte OCR non enregistré pour le souvenir {card.Id} : {ex.Message}");
            }
        }

        return (completed, failed, skipped);
    }

    private async Task<(int Completed, int Failed, int Skipped)> RunVisualHashPassAsync(
        IReadOnlyList<MemoryCard> photos,
        bool skipExisting,
        IProgress<AnalysisProgress>? progress,
        List<string> diagnostics,
        CancellationToken ct)
    {
        int completed = 0;
        int failed = 0;
        int skipped = 0;

        Dictionary<long, ulong>? existing = null;
        if (skipExisting)
        {
            try
            {
                var hashes = await _library.Analysis.GetVisualHashesAsync(ct).ConfigureAwait(false);
                existing = hashes.ToDictionary(static pair => pair.MemoryId, static pair => pair.Hash);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AddDiagnostic(diagnostics, $"Lecture des empreintes existantes impossible : {ex.Message}");
            }
        }

        for (int index = 0; index < photos.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var card = photos[index];

            // Une miniature suffit très largement pour une empreinte dHash 9 × 8 et évite de
            // décoder l'original, souvent plusieurs fois plus lourd.
            string? path = await Task.Run(
                () => ResolveImagePath(card, preferThumbnail: true), ct).ConfigureAwait(false);
            string label = path is null ? $"souvenir {card.Id}" : DisplayName(path);
            progress?.Report(new AnalysisProgress(
                AnalysisPhase.VisualHash, index, photos.Count,
                $"Empreinte visuelle {index + 1}/{photos.Count} — {label}"));

            if (path is null)
            {
                failed++;
                AddDiagnostic(diagnostics, $"Souvenir {card.Id} : aucun fichier image lisible pour l'empreinte.");
                continue;
            }

            if (existing is not null && existing.ContainsKey(card.Id))
            {
                skipped++;
                continue;
            }

            var computed = await Task.Run(
                () =>
                {
                    bool success = VisualHashService.TryComputeFromFile(path, out ulong hash, out string? error);
                    return (Success: success, Hash: hash, Error: error);
                },
                ct).ConfigureAwait(false);

            if (!computed.Success)
            {
                failed++;
                AddDiagnostic(diagnostics, computed.Error ?? $"Empreinte impossible pour « {path} ».");
                continue;
            }

            try
            {
                await _library.Analysis.SetVisualHashAsync(card.Id, computed.Hash, ct).ConfigureAwait(false);
                completed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                AddDiagnostic(diagnostics, $"Empreinte non enregistrée pour le souvenir {card.Id} : {ex.Message}");
            }
        }

        return (completed, failed, skipped);
    }

    private async Task<FaceGroupingResult?> RunFacePassAsync(
        IReadOnlyList<MemoryCard> photos,
        IProgress<AnalysisProgress>? progress,
        List<string> diagnostics,
        CancellationToken ct)
    {
        var sources = new List<(long MemoryId, string Path)>(photos.Count);
        foreach (var card in photos)
        {
            ct.ThrowIfCancellationRequested();
            var path = await Task.Run(
                () => ResolveImagePath(card, preferThumbnail: false), ct).ConfigureAwait(false);
            if (path is null)
            {
                AddDiagnostic(diagnostics, $"Souvenir {card.Id} : aucun fichier image lisible pour les visages.");
                continue;
            }

            sources.Add((card.Id, path));
        }

        if (sources.Count == 0)
        {
            progress?.Report(new AnalysisProgress(
                AnalysisPhase.Faces, 0, 0, "Aucune image disponible pour les visages."));
            return null;
        }

        var relay = new FaceProgressRelay(progress);
        var result = await _faces.RebuildGroupsAsync(_library, sources, relay, ct).ConfigureAwait(false);
        if (result.Cancelled)
        {
            return result;
        }

        if (result.Error is not null)
        {
            AddDiagnostic(diagnostics, result.Error);
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            AddDiagnostic(diagnostics, diagnostic);
        }

        return result;
    }

    private string? ResolveImagePath(MemoryCard card, bool preferThumbnail)
    {
        string? thumbnail = null;
        if (preferThumbnail)
        {
            thumbnail = TryGetThumbnailPath(card);
            if (thumbnail is not null)
            {
                return thumbnail;
            }
        }

        var original = TryGetOriginalPath(card);
        if (original is not null)
        {
            return original;
        }

        return thumbnail ?? TryGetThumbnailPath(card);
    }

    private string? TryGetOriginalPath(MemoryCard card)
    {
        if (card.MainAssetId is not { } assetId)
        {
            return null;
        }

        try
        {
            string path = _library.Catalog.GetAssetPath(assetId);
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private string? TryGetThumbnailPath(MemoryCard card)
    {
        if (!card.HasThumbnail)
        {
            return null;
        }

        try
        {
            return _library.Catalog.GetThumbnailPath(card.Id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

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

    /// <summary>Relais synchrone vers la progression de l'interface, sans capture de contexte UI.</summary>
    private sealed class FaceProgressRelay(IProgress<AnalysisProgress>? target) : IProgress<FaceGroupingProgress>
    {
        public void Report(FaceGroupingProgress value) =>
            target?.Report(new AnalysisProgress(
                AnalysisPhase.Faces, value.Done, value.Total, value.Message));
    }
}
