using SkiaSharp;

namespace Memo.Media;

/// <summary>
/// Image décodée dans son sens d'affichage. <see cref="Bitmap"/> porte les pixels déjà tournés ;
/// <see cref="EncodedWidth"/> et <see cref="EncodedHeight"/> restent les dimensions du fichier source.
/// </summary>
public sealed class DecodedImage : IDisposable
{
    private SKBitmap? _bitmap;

    internal DecodedImage(
        SKBitmap bitmap,
        int encodedWidth,
        int encodedHeight,
        SKEncodedOrigin origin,
        string format,
        bool hasAlpha)
    {
        _bitmap = bitmap;
        EncodedWidth = encodedWidth;
        EncodedHeight = encodedHeight;
        Origin = origin;
        Format = format;
        HasAlpha = hasAlpha;
    }

    /// <summary>Pixels dans le sens d'affichage, prêts pour l'interface ou la composition.</summary>
    public SKBitmap Bitmap => _bitmap ?? throw new ObjectDisposedException(nameof(DecodedImage));

    /// <summary>Largeur telle qu'encodée dans le fichier, avant rotation.</summary>
    public int EncodedWidth { get; }

    /// <summary>Hauteur telle qu'encodée dans le fichier, avant rotation.</summary>
    public int EncodedHeight { get; }

    public int DisplayWidth => Bitmap.Width;

    public int DisplayHeight => Bitmap.Height;

    /// <summary>Orientation EXIF effectivement appliquée, une seule fois.</summary>
    public SKEncodedOrigin Origin { get; }

    /// <summary>Rotation d'affichage approximative en degrés (0, 90, 180 ou 270).</summary>
    public int Rotation => ExifOrientation.ToRotationDegrees(Origin);

    /// <summary>Nom du format réellement détecté (jpeg, png ou webp).</summary>
    public string Format { get; }

    /// <summary>Vrai si le format source porte un canal alpha, même si tous les pixels sont opaques.</summary>
    public bool HasAlpha { get; }

    public bool IsDisposed => _bitmap is null;

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

/// <summary>Décodage JPEG, PNG et WebP via SkiaSharp, avec application unique de l'orientation EXIF.</summary>
public static class ImageDecoder
{
    /// <summary>Décode un fichier image. Lève <see cref="MediaDecodeException"/> si le contenu n'est pas décodable.</summary>
    public static DecodedImage Decode(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Le chemin du fichier est vide.", nameof(path));
        }

        byte[] encoded;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.SequentialScan);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            encoded = memory.ToArray();
        }
        catch (FileNotFoundException ex)
        {
            throw new MediaDecodeException($"Le fichier « {path} » est introuvable.", "fichier-introuvable", path, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new MediaDecodeException($"Le fichier « {path} » est introuvable.", "fichier-introuvable", path, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MediaDecodeException(
                $"Le fichier « {path} » ne peut pas être lu : accès refusé.",
                "fichier-acces-refuse",
                path,
                ex);
        }
        catch (IOException ex)
        {
            throw new MediaDecodeException(
                $"Le fichier « {path} » est verrouillé ou illisible.",
                "fichier-illisible",
                path,
                ex);
        }

        return DecodeBytes(encoded, path);
    }

    /// <summary>Décode des octets en mémoire. Le libellé sert uniquement aux messages d'erreur.</summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> encodedData, string? sourcePath = null)
    {
        if (encodedData.IsEmpty)
        {
            throw new MediaDecodeException(
                "Les données fournies sont vides : aucune image ne peut être décodée.",
                "donnees-vides",
                sourcePath);
        }

        return DecodeBytes(encodedData, sourcePath);
    }

    private static DecodedImage DecodeBytes(ReadOnlySpan<byte> encoded, string? sourcePath)
    {
        var format = MediaFormatDetector.Detect(encoded);
        string subject = Subject(sourcePath);

        if (!format.IsKnown)
        {
            throw new MediaDecodeException(
                $"{subject} ne correspond à aucune signature JPEG, PNG ou WebP reconnue.",
                "format-inconnu",
                sourcePath);
        }

        if (format.IsVideo)
        {
            throw new MediaDecodeException(
                $"{subject} est une vidéo {format.Name.ToUpperInvariant()}, pas une image.",
                "format-video",
                sourcePath);
        }

        if (format.Format is not (MediaFormatKind.Jpeg or MediaFormatKind.Png or MediaFormatKind.WebP))
        {
            throw new MediaDecodeException(
                $"{subject} est au format {format.Name.ToUpperInvariant()}, reconnu mais non décodable par ce module d'images.",
                "format-non-decode",
                sourcePath);
        }

        SKData? data = null;
        SKCodec? codec = null;
        SKBitmap? bitmap = null;
        try
        {
            data = SKData.CreateCopy(encoded);
            codec = SKCodec.Create(data);
            if (codec is null)
            {
                throw new MediaDecodeException(
                    $"{subject} n'a pas pu être ouvert par le décodeur d'images.",
                    "decodage-impossible",
                    sourcePath);
            }

            SKImageInfo info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0)
            {
                throw new MediaDecodeException(
                    $"{subject} annonce des dimensions invalides ({info.Width}×{info.Height}).",
                    "dimensions-invalides",
                    sourcePath);
            }

            var alphaType = info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
            var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, alphaType);
            bitmap = new SKBitmap(decodeInfo);

            SKCodecResult result = codec.GetPixels(decodeInfo, bitmap.GetPixels());
            if (result != SKCodecResult.Success)
            {
                throw DecodeResultException(result, subject, sourcePath);
            }

            SKEncodedOrigin origin = ExifOrientationReader.Read(encoded, format.Format);
            if (origin == SKEncodedOrigin.Default)
            {
                origin = codec.EncodedOrigin;
            }

            if (origin == SKEncodedOrigin.Default)
            {
                origin = SKEncodedOrigin.TopLeft;
            }

            SKBitmap display = ExifOrientation.Apply(bitmap, origin);
            if (!ReferenceEquals(display, bitmap))
            {
                bitmap.Dispose();
            }

            bitmap = null;
            return new DecodedImage(
                display,
                info.Width,
                info.Height,
                origin,
                format.Name,
                info.AlphaType != SKAlphaType.Opaque);
        }
        catch (MediaDecodeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            throw new MediaDecodeException(
                "Le moteur d'images SkiaSharp n'est pas disponible. Les fichiers natifs de l'application sont peut-être incomplets.",
                "moteur-images-indisponible",
                sourcePath,
                ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new MediaDecodeException(
                $"{subject} n'a pas pu être décodé. Le fichier est peut-être corrompu ou dans une variante non prise en charge.",
                "decodage-impossible",
                sourcePath,
                ex);
        }
        finally
        {
            bitmap?.Dispose();
            codec?.Dispose();
            data?.Dispose();
        }
    }

    private static MediaDecodeException DecodeResultException(
        SKCodecResult result,
        string subject,
        string? sourcePath) => result switch
    {
        SKCodecResult.IncompleteInput => new MediaDecodeException(
            $"{subject} est incomplet : le décodage s'est arrêté avant la fin des données.",
            "image-incomplete",
            sourcePath),

        SKCodecResult.ErrorInInput or SKCodecResult.InvalidInput => new MediaDecodeException(
            $"{subject} contient des données corrompues ou tronquées.",
            "image-corrompue",
            sourcePath),

        SKCodecResult.Unimplemented => new MediaDecodeException(
            $"{subject} utilise une variante de format non prise en charge par le décodeur.",
            "format-non-prise-en-charge",
            sourcePath),

        _ => new MediaDecodeException(
            $"{subject} n'a pas pu être décodé (code du décodeur : {result}).",
            "decodage-impossible",
            sourcePath),
    };

    private static string Subject(string? sourcePath) =>
        string.IsNullOrWhiteSpace(sourcePath) ? "Les données fournies" : $"Le fichier « {sourcePath} »";
}
