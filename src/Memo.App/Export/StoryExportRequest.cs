using Memo.Core;

namespace Memo.App.Export;

/// <summary>
/// Qualité de l'export vidéo. La valeur désigne le petit côté de l'image :
/// 1080p donne 1080×1920 en portrait et 1920×1080 en paysage ;
/// 720p donne 720×1280 en portrait et 1280×720 en paysage.
/// </summary>
public enum StoryExportQuality
{
    /// <summary>1080p : 1080×1920 en portrait, 1920×1080 en paysage.</summary>
    Hd1080 = 0,

    /// <summary>720p : 720×1280 en portrait, 1280×720 en paysage.</summary>
    Hd720 = 1,
}

/// <summary>Étapes exposées par la progression d'un export de Story.</summary>
public enum StoryExportPhase
{
    /// <summary>Validation des entrées et ouverture des médias par Media Foundation.</summary>
    Preparing,

    /// <summary>Rendu MP4 par Media Foundation.</summary>
    Rendering,

    /// <summary>Vérification du fichier produit et renommage atomique.</summary>
    Finalizing,

    /// <summary>Export terminé et fichier disponible à l'emplacement demandé.</summary>
    Completed,

    /// <summary>Export annulé ; aucun fichier final n'a été produit.</summary>
    Cancelled,

    /// <summary>Export en échec ; aucun fichier final n'a été produit.</summary>
    Failed,
}

/// <summary>
/// Un élément de Story à exporter. <see cref="MainPath"/> désigne la photo ou la vidéo,
/// <see cref="OverlayPath"/> le calque optionnel. <see cref="Width"/> et <see cref="Height"/>
/// sont les dimensions d'affichage (rotation EXIF comprise), et non les dimensions encodées.
/// <see cref="DurationSeconds"/> sert d'estimation pour les vidéos ; la durée réelle du clip
/// reste celle du fichier. Pour une photo, la durée vient de
/// <see cref="StoryExportRequest.PhotoDurationSeconds"/>.
/// </summary>
public sealed record StoryExportItem(
    string MainPath,
    string? OverlayPath,
    MediaKind MediaKind,
    double DurationSeconds,
    int Width,
    int Height)
{
    /// <summary>Vrai lorsque les dimensions connues sont plus larges que hautes.</summary>
    public bool IsLandscape => Width > 0 && Height > 0 && Width > Height;
}

/// <summary>
/// Demande d'export d'une Story ou d'un récapitulatif en un seul MP4 H.264/AAC.
/// L'ordre de <see cref="Items"/> est l'ordre de lecture. Le fichier de sortie ne remplace
/// jamais une source ; l'écrasement d'une sortie existante exige
/// <see cref="OverwriteExistingFile"/>.
/// </summary>
public sealed record StoryExportRequest(
    IReadOnlyList<StoryExportItem> Items,
    string OutputPath,
    string? Title = null,
    StoryExportQuality Quality = StoryExportQuality.Hd1080,
    bool IncludeAudio = true,
    double PhotoDurationSeconds = 4.0,
    bool OverwriteExistingFile = false);

/// <summary>
/// Progression d'un export. <see cref="Percent"/> est un pourcentage global de 0 à 100,
/// <see cref="CurrentItem"/> le chemin du média en cours et <see cref="ItemIndex"/> son
/// numéro à partir de 1 (0 lorsque l'étape ne concerne aucun élément en particulier).
/// </summary>
public sealed record StoryExportProgress(
    StoryExportPhase Phase,
    double Percent,
    string? CurrentItem,
    int ItemIndex,
    int ItemCount,
    string Message);

/// <summary>
/// Résultat d'un export. En cas d'échec ou d'annulation, <see cref="OutputPath"/> est nul et
/// <see cref="ErrorMessage"/> explique précisément la cause ; aucun fichier final n'est alors
/// présenté comme réussi. <see cref="Warnings"/> contient les remarques non bloquantes
/// (HEVC détecté, photo convertie temporairement, espace disque non vérifiable, etc.).
/// </summary>
public sealed record StoryExportResult(
    bool Succeeded,
    string? OutputPath,
    TimeSpan Duration,
    long SizeBytes,
    string? ErrorMessage,
    bool Cancelled,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Construit un échec lisible, sans fichier de sortie.</summary>
    public static StoryExportResult Failure(
        string errorMessage,
        IReadOnlyList<string>? warnings = null,
        TimeSpan duration = default,
        bool cancelled = false) =>
        new(
            Succeeded: false,
            OutputPath: null,
            Duration: duration,
            SizeBytes: 0,
            ErrorMessage: errorMessage,
            Cancelled: cancelled,
            Warnings: warnings ?? Array.Empty<string>());

    /// <summary>Construit un succès vers un fichier existant et vérifié.</summary>
    public static StoryExportResult Success(
        string outputPath,
        TimeSpan duration,
        long sizeBytes,
        IReadOnlyList<string>? warnings = null) =>
        new(
            Succeeded: true,
            OutputPath: outputPath,
            Duration: duration,
            SizeBytes: sizeBytes,
            ErrorMessage: null,
            Cancelled: false,
            Warnings: warnings ?? Array.Empty<string>());
}
