using System.Globalization;
using Memo.Core;
using Memo.Media;

namespace Memo.App.Export;

/// <summary>
/// Élément de Story proposé à la prévisualisation. Les chemins sont déjà résolus par
/// l'appelant ; <see cref="OverlayDeclared"/> distingue un calque absent d'un calque
/// annoncé mais introuvable.
/// </summary>
public sealed record StoryExportPreviewSource(
    int Number,
    string Title,
    MediaKind Kind,
    string? MainPath,
    string? OverlayPath,
    bool OverlayDeclared,
    long? DurationMs,
    int? Width,
    int? Height);

/// <summary>
/// Résultat de l'inspection d'un élément : chemins exploitables, dimensions d'affichage,
/// durée, présence d'un calque et d'une piste audio, avertissements associés.
/// </summary>
public sealed record StoryExportPreviewAnalysis(
    StoryExportPreviewSource Source,
    bool IsExportable,
    string? MainPath,
    string? OverlayPath,
    bool HasOverlay,
    bool HasAudio,
    double VideoDurationSeconds,
    bool DurationKnown,
    int Width,
    int Height,
    string? Warning);

/// <summary>Ligne prête à afficher dans le panneau de prévisualisation.</summary>
public sealed record StoryExportPreviewRow(
    int Number,
    string Title,
    MediaKind Kind,
    string FormatLabel,
    string FileName,
    string DurationLabel,
    bool HasOverlay,
    bool HasAudio,
    string? Warning);

/// <summary>
/// Aperçu cohérent avec la requête qui sera remise à <see cref="StoryExporter"/> :
/// mêmes éléments, même ordre, mêmes options et la même estimation de débit.
/// </summary>
public sealed record StoryExportPreviewPlan(
    IReadOnlyList<StoryExportPreviewRow> Rows,
    IReadOnlyList<StoryExportItem> Items,
    double DurationSeconds,
    long EstimatedBytes,
    bool HasAudioTrack,
    int PhotoCount,
    int VideoCount,
    int UnavailableCount,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Vrai dès qu'au moins un média possède un fichier source exploitable.</summary>
    public bool CanExport => Items.Count > 0;
}

/// <summary>
/// Construit l'aperçu d'un export de Story sans effectuer le rendu. L'analyse se contente
/// de lire les en-têtes avec <see cref="MediaInspector"/> et <see cref="DisplayGeometry"/> :
/// elle ne décode pas les pixels et ne vérifie donc pas la présence des codecs.
/// </summary>
public static class StoryExportPreviewBuilder
{
    private const double UnknownVideoDurationSeconds = 30;

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>
    /// Inspecte chaque source dans l'ordre de lecture. La progression rapporte le nombre
    /// d'éléments analysés ; l'analyse reste annulable.
    /// </summary>
    public static async Task<IReadOnlyList<StoryExportPreviewAnalysis>> AnalyzeAsync(
        IReadOnlyList<StoryExportPreviewSource> sources,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var analyses = new List<StoryExportPreviewAnalysis>(sources.Count);
        for (int index = 0; index < sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoryExportPreviewSource source = sources[index];

            // L'inspection lit les en-têtes : elle reste rapide même pour une longue Story.
            StoryExportPreviewAnalysis analysis = await Task
                .Run(() => Analyze(source), cancellationToken)
                .ConfigureAwait(false);

            analyses.Add(analysis);
            progress?.Report(index + 1);
        }

        return analyses;
    }

    /// <summary>
    /// Applique les options choisies à l'analyse et produit les lignes affichables ainsi que
    /// les éléments exacts de la future requête d'export.
    /// </summary>
    public static StoryExportPreviewPlan CreatePlan(
        IReadOnlyList<StoryExportPreviewAnalysis> analyses,
        StoryExportQuality quality,
        bool includeAudio,
        double photoDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(analyses);

        double photoSeconds = double.IsFinite(photoDurationSeconds)
            ? Math.Clamp(photoDurationSeconds, 0.1, 600)
            : 4;

        var rows = new List<StoryExportPreviewRow>(analyses.Count);
        var items = new List<StoryExportItem>(analyses.Count);
        var warnings = new List<string>();
        var totalSeconds = 0.0;
        var photoCount = 0;
        var videoCount = 0;
        var unavailableCount = 0;
        var anyVideoAudio = false;

        foreach (StoryExportPreviewAnalysis analysis in analyses)
        {
            StoryExportPreviewSource source = analysis.Source;
            if (!analysis.IsExportable || analysis.MainPath is null)
            {
                unavailableCount++;
                rows.Add(new StoryExportPreviewRow(
                    source.Number,
                    source.Title,
                    source.Kind,
                    "format inconnu",
                    "—",
                    "—",
                    HasOverlay: false,
                    HasAudio: false,
                    analysis.Warning));
                AddWarning(warnings, source.Number, analysis.Warning);
                continue;
            }

            double itemSeconds;
            string durationLabel;
            if (source.Kind == MediaKind.Photo)
            {
                photoCount++;
                itemSeconds = photoSeconds;
                durationLabel = FormatSeconds(photoSeconds);
            }
            else
            {
                videoCount++;
                itemSeconds = analysis.DurationKnown
                    ? analysis.VideoDurationSeconds
                    : UnknownVideoDurationSeconds;
                durationLabel = analysis.DurationKnown
                    ? FormatDuration(itemSeconds)
                    : $"≈ {FormatDuration(UnknownVideoDurationSeconds)}";
                anyVideoAudio |= analysis.HasAudio;
            }

            totalSeconds += Math.Max(itemSeconds, 0.1);

            // DuréeSeconds n'est qu'une indication pour les photos ; la durée réelle est
            // portée par PhotoDurationSeconds dans la requête.
            items.Add(new StoryExportItem(
                analysis.MainPath,
                analysis.OverlayPath,
                source.Kind,
                source.Kind == MediaKind.Photo ? 0 : itemSeconds,
                analysis.Width,
                analysis.Height));

            rows.Add(new StoryExportPreviewRow(
                source.Number,
                source.Title,
                source.Kind,
                FormatFormat(analysis),
                Path.GetFileName(analysis.MainPath),
                durationLabel,
                analysis.HasOverlay,
                analysis.HasAudio,
                analysis.Warning));

            AddWarning(warnings, source.Number, analysis.Warning);
        }

        bool hasAudioTrack = includeAudio && anyVideoAudio;
        if (includeAudio && videoCount > 0 && !anyVideoAudio)
        {
            warnings.Add(
                "Aucune vidéo de cette Story n'a de piste audio : le fichier MP4 sera muet, " +
                "même avec l'option audio activée.");
        }

        if (items.Count == 0 && warnings.Count == 0)
        {
            warnings.Add("Aucun média lisible : l'export est impossible.");
        }

        return new StoryExportPreviewPlan(
            rows,
            items,
            totalSeconds,
            EstimateBytes(totalSeconds, quality, hasAudioTrack),
            hasAudioTrack,
            photoCount,
            videoCount,
            unavailableCount,
            warnings);
    }

    /// <summary>Formate une durée de Story en minutes et secondes, ou en heures si besoin.</summary>
    public static string FormatDuration(double seconds)
    {
        TimeSpan total = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return total.TotalHours >= 1
            ? $"{(int)total.TotalHours}:{total.Minutes:00}:{total.Seconds:00}"
            : $"{(int)total.TotalMinutes}:{total.Seconds:00}";
    }

    /// <summary>Formate une taille estimée avec une unité lisible.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes)} o";
        }

        if (bytes < 1024L * 1024)
        {
            return $"{Math.Max(1, bytes / 1024)} Ko";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return (bytes / (1024.0 * 1024)).ToString("0.#", French) + " Mo";
        }

        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", French) + " Go";
    }

    /// <summary>Formate une durée d'affichage de photo, par exemple « 4,5 s ».</summary>
    public static string FormatSeconds(double seconds) =>
        seconds.ToString("0.#", French) + " s";

    private static StoryExportPreviewAnalysis Analyze(StoryExportPreviewSource source)
    {
        var warnings = new List<string>();
        var exportable = true;
        string? mainPath = source.MainPath;
        MediaInfo? info = null;
        var width = 0;
        var height = 0;
        var hasAudio = false;
        double videoDuration = 0;
        bool durationKnown = source.Kind != MediaKind.Video;

        if (string.IsNullOrWhiteSpace(mainPath) || !File.Exists(mainPath))
        {
            exportable = false;
            warnings.Add("Média indisponible : le fichier source est introuvable.");
        }
        else
        {
            try
            {
                info = MediaInspector.InspectFile(mainPath);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Un en-tête illisible n'empêche pas forcément Media Foundation de décoder :
                // l'élément reste proposé, avec un avertissement explicite.
                warnings.Add($"En-tête illisible ({Describe(ex)}) : le décodage peut échouer.");
            }
        }

        if (info is not null)
        {
            DisplayDimensions display = info.Width is > 0 && info.Height is > 0
                ? DisplayGeometry.GetDisplaySize(info)
                : new DisplayDimensions(0, 0);
            width = display.Width;
            height = display.Height;

            if (display.IsEmpty)
            {
                if (source.Width is > 0 && source.Height is > 0)
                {
                    width = source.Width.Value;
                    height = source.Height.Value;
                    warnings.Add("Dimensions absentes de l'en-tête ; celles de la fiche sont utilisées.");
                }
                else
                {
                    warnings.Add("Dimensions inconnues : le média risque d'être refusé au décodage.");
                }
            }

            if (string.Equals(info.Format, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("Format non reconnu : le média risque d'être refusé au décodage.");
            }
            else if (source.Kind == MediaKind.Photo &&
                     (string.Equals(info.Format, "heic", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(info.Format, "avif", StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add(
                    $"Format {info.Format.ToUpperInvariant()} rarement décodé par Media Foundation " +
                    "sans l'extension Windows correspondante.");
            }

            if (source.Kind == MediaKind.Video)
            {
                hasAudio = info.HasAudio;
                long? durationMs = info.DurationMs is > 0 ? info.DurationMs : source.DurationMs;
                if (durationMs is > 0)
                {
                    videoDuration = durationMs.Value / 1000.0;
                    durationKnown = true;
                }
                else
                {
                    videoDuration = UnknownVideoDurationSeconds;
                    warnings.Add("Durée inconnue : une estimation prudente de 30 s est utilisée.");
                }
            }
        }
        else
        {
            if (source.Width is > 0 && source.Height is > 0)
            {
                width = source.Width.Value;
                height = source.Height.Value;
            }

            if (source.Kind == MediaKind.Video && source.DurationMs is > 0)
            {
                videoDuration = source.DurationMs.Value / 1000.0;
                durationKnown = true;
            }
        }

        string? overlayPath = source.OverlayPath;
        var hasOverlay = false;
        if (source.OverlayDeclared)
        {
            if (string.IsNullOrWhiteSpace(overlayPath) || !File.Exists(overlayPath))
            {
                warnings.Add("Calque introuvable : il sera ignoré.");
                overlayPath = null;
            }
            else
            {
                try
                {
                    MediaInfo overlayInfo = MediaInspector.InspectFile(overlayPath);
                    DisplayDimensions overlaySize = DisplayGeometry.GetDisplaySize(overlayInfo);
                    if (overlaySize.IsEmpty)
                    {
                        warnings.Add("Dimensions du calque inconnues : il sera ignoré.");
                        overlayPath = null;
                    }
                    else
                    {
                        hasOverlay = true;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    warnings.Add($"Calque illisible ({Describe(ex)}) : il sera ignoré.");
                    overlayPath = null;
                }
            }
        }

        return new StoryExportPreviewAnalysis(
            source,
            exportable,
            exportable ? mainPath : null,
            overlayPath,
            hasOverlay,
            hasAudio,
            videoDuration,
            durationKnown,
            width,
            height,
            warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    private static long EstimateBytes(
        double seconds,
        StoryExportQuality quality,
        bool hasAudioTrack)
    {
        // Mêmes débits que StoryExporter (8 Mbit/s en 1080p, 5 Mbit/s en 720p) et même
        // majoration de 25 % pour le conteneur, sans la marge de sécurité disque.
        double videoBitrate = quality == StoryExportQuality.Hd720 ? 5_000_000 : 8_000_000;
        double audioBitrate = hasAudioTrack ? 192_000 : 0;
        double bytes = Math.Max(seconds, 0.1) * (videoBitrate + audioBitrate) / 8.0 * 1.25;
        return (long)Math.Ceiling(bytes);
    }

    private static string FormatFormat(StoryExportPreviewAnalysis analysis)
    {
        if (analysis.Width <= 0 || analysis.Height <= 0)
        {
            return "format inconnu";
        }

        string orientation = analysis.Width > analysis.Height
            ? "paysage"
            : analysis.Width == analysis.Height
                ? "carré"
                : "portrait";

        return $"{analysis.Width}×{analysis.Height} · {orientation}";
    }

    private static void AddWarning(List<string> warnings, int number, string? warning)
    {
        if (!string.IsNullOrWhiteSpace(warning))
        {
            warnings.Add($"Élément {number} : {warning}");
        }
    }

    private static string Describe(Exception exception)
    {
        Exception root = exception.GetBaseException();
        return string.IsNullOrWhiteSpace(root.Message) ? root.GetType().Name : root.Message;
    }
}
