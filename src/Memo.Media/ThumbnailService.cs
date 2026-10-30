using SkiaSharp;

namespace Memo.Media;

/// <summary>
/// Miniatures dérivées. La source est seulement lue ; l'écriture passe par un fichier temporaire
/// déplacé à la fin, afin qu'une miniature interrompue ne laisse pas de JPEG partiel.
/// </summary>
public static class ThumbnailService
{
    public const int DefaultMaxSize = 512;
    public const int JpegQuality = 82;

    /// <summary>Fond neutre appliqué aux zones transparentes d'une miniature JPEG.</summary>
    public static readonly SKColor NeutralBackground = new(0xF2, 0xF2, 0xF2);

    /// <summary>Position par défaut de l'image extraite d'une vidéo.</summary>
    public static readonly TimeSpan DefaultVideoFrameAt = TimeSpan.FromSeconds(0.4);

    /// <summary>Délai maximal d'attente de la première image vidéo.</summary>
    public static readonly TimeSpan VideoFrameTimeout = TimeSpan.FromSeconds(8);

    public static bool CreateImageThumbnail(string sourcePath, string destinationPath, int maxSize = DefaultMaxSize) =>
        CreateImageThumbnail(sourcePath, destinationPath, maxSize, out _);

    /// <summary>
    /// Crée une miniature JPEG (qualité 82) dont le plus grand côté ne dépasse pas <paramref name="maxSize"/>.
    /// Une petite image n'est pas agrandie. Le ratio est préservé et la transparence est composée sur
    /// <see cref="NeutralBackground"/>.
    /// </summary>
    public static bool CreateImageThumbnail(
        string sourcePath,
        string destinationPath,
        int maxSize,
        out string? diagnostic) =>
        TryCreateImageThumbnail(sourcePath, destinationPath, maxSize, out diagnostic);

    public static bool CreateVideoThumbnail(
        string sourcePath,
        string destinationPath,
        int maxSize = DefaultMaxSize,
        TimeSpan? frameAt = null) =>
        CreateVideoThumbnail(sourcePath, destinationPath, maxSize, frameAt, out _);

    /// <summary>
    /// Extrait une image à <paramref name="frameAt"/> (0,4 s par défaut) puis la réduit comme une image.
    /// Le grabber est créé et libéré à chaque appel ; pour un lot, préférez la surcharge qui reçoit
    /// un <see cref="VideoFrameGrabber"/> réutilisable.
    /// </summary>
    public static bool CreateVideoThumbnail(
        string sourcePath,
        string destinationPath,
        int maxSize,
        TimeSpan? frameAt,
        out string? diagnostic) =>
        TryCreateVideoThumbnail(null, sourcePath, destinationPath, maxSize, frameAt, out diagnostic);

    public static bool CreateVideoThumbnail(
        VideoFrameGrabber grabber,
        string sourcePath,
        string destinationPath,
        int maxSize = DefaultMaxSize,
        TimeSpan? frameAt = null) =>
        CreateVideoThumbnail(grabber, sourcePath, destinationPath, maxSize, frameAt, out _);

    /// <summary>Surcharge réutilisable : le grabber fourni n'est pas libéré par cette méthode.</summary>
    public static bool CreateVideoThumbnail(
        VideoFrameGrabber grabber,
        string sourcePath,
        string destinationPath,
        int maxSize,
        TimeSpan? frameAt,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(grabber);
        return TryCreateVideoThumbnail(grabber, sourcePath, destinationPath, maxSize, frameAt, out diagnostic);
    }

    private static bool TryCreateImageThumbnail(
        string sourcePath,
        string destinationPath,
        int maxSize,
        out string? diagnostic)
    {
        if (!ValidateRequest(sourcePath, destinationPath, maxSize, "miniature", out diagnostic))
        {
            return false;
        }

        try
        {
            using var decoded = ImageDecoder.Decode(sourcePath);
            using var thumbnail = RenderThumbnail(decoded.Bitmap, maxSize);
            AtomicFile.WriteImage(thumbnail, destinationPath, SKEncodedImageFormat.Jpeg, JpegQuality);
            diagnostic = null;
            return true;
        }
        catch (MediaDecodeException ex)
        {
            diagnostic = $"{ex.Message} Aucune miniature n'a été écrite dans « {destinationPath} ».";
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            diagnostic = FailureMessage(sourcePath, destinationPath, ex);
            return false;
        }
    }

    private static bool TryCreateVideoThumbnail(
        VideoFrameGrabber? externalGrabber,
        string sourcePath,
        string destinationPath,
        int maxSize,
        TimeSpan? frameAt,
        out string? diagnostic)
    {
        if (!ValidateRequest(sourcePath, destinationPath, maxSize, "image vidéo", out diagnostic))
        {
            return false;
        }

        if (frameAt is { } requested && requested < TimeSpan.Zero)
        {
            diagnostic = "La position demandée pour l'image vidéo ne peut pas être négative.";
            return false;
        }

        VideoFrameGrabber? ownedGrabber = null;
        VideoFrameGrabber grabber;
        if (externalGrabber is null)
        {
            try
            {
                ownedGrabber = new VideoFrameGrabber();
                grabber = ownedGrabber;
            }
            catch (MediaDecodeException ex)
            {
                diagnostic = $"{ex.Message} Aucune miniature n'a été écrite dans « {destinationPath} ».";
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                diagnostic = FailureMessage(sourcePath, destinationPath, ex);
                return false;
            }
        }
        else
        {
            grabber = externalGrabber;
        }

        try
        {
            using var frame = grabber.GrabFrame(
                sourcePath,
                frameAt ?? DefaultVideoFrameAt,
                VideoFrameTimeout);

            if (frame is null)
            {
                diagnostic =
                    $"{grabber.LastError ?? $"Aucune image n'a pu être extraite de « {sourcePath} »."} " +
                    $"Aucune miniature n'a été écrite dans « {destinationPath} ».";
                return false;
            }

            using var thumbnail = RenderThumbnail(frame, maxSize);
            AtomicFile.WriteImage(thumbnail, destinationPath, SKEncodedImageFormat.Jpeg, JpegQuality);
            diagnostic = null;
            return true;
        }
        catch (MediaDecodeException ex)
        {
            diagnostic = $"{ex.Message} Aucune miniature n'a été écrite dans « {destinationPath} ».";
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            diagnostic = FailureMessage(sourcePath, destinationPath, ex);
            return false;
        }
        finally
        {
            ownedGrabber?.Dispose();
        }
    }

    private static bool ValidateRequest(
        string sourcePath,
        string destinationPath,
        int maxSize,
        string outputLabel,
        out string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            diagnostic = "Le chemin de la source est vide.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            diagnostic = $"Le chemin de destination de la {outputLabel} est vide.";
            return false;
        }

        if (maxSize <= 0)
        {
            diagnostic = $"La taille maximale de la {outputLabel} doit être strictement positive (valeur reçue : {maxSize}).";
            return false;
        }

        if (IsSamePath(sourcePath, destinationPath))
        {
            diagnostic =
                $"La {outputLabel} ne peut pas remplacer la source. Choisissez une destination différente de « {sourcePath} ».";
            return false;
        }

        diagnostic = null;
        return true;
    }

    private static SKBitmap RenderThumbnail(SKBitmap source, int maxSize)
    {
        double scale = Math.Min(1.0, maxSize / (double)Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var thumbnail = new SKBitmap(info);
        try
        {
            using var canvas = new SKCanvas(thumbnail);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.Clear(NeutralBackground);
            canvas.DrawBitmap(
                source,
                new SKRect(0, 0, width, height),
                new SKSamplingOptions(SKCubicResampler.Mitchell),
                paint);
            canvas.Flush();
            return thumbnail;
        }
        catch
        {
            thumbnail.Dispose();
            throw;
        }
    }

    private static bool IsSamePath(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string FailureMessage(string sourcePath, string destinationPath, Exception ex) =>
        $"La miniature de « {sourcePath} » n'a pas pu être créée dans « {destinationPath} » : {ex.Message}";
}
