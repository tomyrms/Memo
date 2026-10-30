namespace Memo.Media;

/// <summary>
/// Erreur de lecture, de détection ou de décodage d'un média. Le message est destiné à être
/// affiché tel quel dans l'interface ; <see cref="Code"/> reste stable pour les diagnostics.
/// </summary>
public sealed class MediaDecodeException : Exception
{
    public MediaDecodeException(
        string message,
        string code = "media-indisponible",
        string? sourcePath = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        SourcePath = sourcePath;
    }

    /// <summary>Code technique stable, par exemple <c>fichier-introuvable</c> ou <c>image-incomplete</c>.</summary>
    public string Code { get; }

    /// <summary>Chemin de la source concernée, lorsque l'erreur se rapporte à un fichier.</summary>
    public string? SourcePath { get; }
}
