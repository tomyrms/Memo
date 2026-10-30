using SkiaSharp;

namespace Memo.App.Analysis;

/// <summary>
/// Conversions d'images partagées par les analyses locales. Toutes les opérations restent en
/// mémoire : aucun fichier dérivé n'est écrit et aucune image n'est envoyée sur le réseau.
/// </summary>
internal static class AnalysisImage
{
    /// <summary>
    /// Fond opaque appliqué aux zones transparentes. L'OCR et la détection de visages travaillent
    /// sur des images opaques, et un fond clair améliore le contraste du texte sombre.
    /// </summary>
    public static readonly SKColor OpaqueBackground = SKColors.White;

    /// <summary>
    /// Copie l'image sur un fond opaque, dans le sens d'affichage, en limitant son plus grand côté
    /// à <paramref name="maxDimension"/>. Une petite image n'est jamais agrandie.
    /// Le résultat est toujours en BGRA8888 opaque, format attendu par les API Windows.
    /// </summary>
    public static SKBitmap RenderOpaque(SKBitmap source, int maxDimension)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDimension), "La taille maximale doit être positive.");
        }

        if (source.Width <= 0 || source.Height <= 0)
        {
            throw new ArgumentException("L'image source est vide.", nameof(source));
        }

        double scale = Math.Min(1.0, maxDimension / (double)Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var target = new SKBitmap(info);
        try
        {
            using var canvas = new SKCanvas(target);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.Clear(OpaqueBackground);
            canvas.DrawBitmap(
                source,
                new SKRect(0, 0, source.Width, source.Height),
                new SKRect(0, 0, width, height),
                new SKSamplingOptions(SKCubicResampler.Mitchell),
                paint);
            canvas.Flush();
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Extrait les pixels BGRA dans un tableau compact (sans remplissage de fin de ligne).
    /// <see cref="SoftwareBitmap"/> et <c>Windows.Media.Ocr</c> attendent exactement
    /// <c>largeur × hauteur × 4</c> octets.
    /// </summary>
    public static byte[] ToBgraBytes(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.ColorType != SKColorType.Bgra8888)
        {
            throw new ArgumentException("L'image doit être en BGRA8888.", nameof(bitmap));
        }

        int width = bitmap.Width;
        int height = bitmap.Height;
        int rowBytes = checked(width * 4);
        var bytes = new byte[checked(rowBytes * height)];
        var pixels = bitmap.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            pixels.Slice(y * bitmap.RowBytes, rowBytes).CopyTo(bytes.AsSpan(y * rowBytes, rowBytes));
        }

        return bytes;
    }

    /// <summary>
    /// Convertit une image BGRA en luminance 8 bits (Rec. 601). Le détecteur de visages Windows
    /// accepte ce format, plus léger qu'une image couleur.
    /// </summary>
    public static byte[] ToGray8Bytes(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.ColorType != SKColorType.Bgra8888)
        {
            throw new ArgumentException("L'image doit être en BGRA8888.", nameof(bitmap));
        }

        int width = bitmap.Width;
        int height = bitmap.Height;
        var bytes = new byte[checked(width * height)];
        var pixels = bitmap.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * bitmap.RowBytes;
            int targetRow = y * width;
            for (int x = 0; x < width; x++)
            {
                int offset = sourceRow + (x * 4);
                int b = pixels[offset];
                int g = pixels[offset + 1];
                int r = pixels[offset + 2];
                bytes[targetRow + x] = (byte)(((r * 299) + (g * 587) + (b * 114) + 500) / 1000);
            }
        }

        return bytes;
    }

    /// <summary>
    /// Construit un carré centré sur un visage, avec une marge, sans sortir de l'image.
    /// Un carré rend le descripteur moins sensible aux variations de cadrage entre deux photos.
    /// </summary>
    public static SKRectI SquareRegion(
        int imageWidth,
        int imageHeight,
        int x,
        int y,
        int width,
        int height,
        double paddingRatio)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "L'image doit avoir des dimensions positives.");
        }

        int safeWidth = Math.Clamp(width, 1, imageWidth);
        int safeHeight = Math.Clamp(height, 1, imageHeight);
        int safeX = Math.Clamp(x, 0, imageWidth - safeWidth);
        int safeY = Math.Clamp(y, 0, imageHeight - safeHeight);

        double padding = Math.Clamp(paddingRatio, 0.0, 1.0);
        double side = Math.Max(safeWidth, safeHeight) * (1.0 + (2.0 * padding));
        int maxSide = Math.Min(imageWidth, imageHeight);
        int sidePixels = Math.Clamp(
            (int)Math.Round(side, MidpointRounding.AwayFromZero),
            1,
            maxSide);

        double centerX = safeX + (safeWidth / 2.0);
        double centerY = safeY + (safeHeight / 2.0);
        int left = (int)Math.Round(centerX - (sidePixels / 2.0), MidpointRounding.AwayFromZero);
        int top = (int)Math.Round(centerY - (sidePixels / 2.0), MidpointRounding.AwayFromZero);
        left = Math.Clamp(left, 0, imageWidth - sidePixels);
        top = Math.Clamp(top, 0, imageHeight - sidePixels);

        return new SKRectI(left, top, left + sidePixels, top + sidePixels);
    }
}
