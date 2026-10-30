using SkiaSharp;

namespace Memo.Media;

/// <summary>
/// Composition d'un média et de son calque dans l'espace d'affichage. Le média est décodé avec son
/// orientation EXIF, puis le calque est ajusté proportionnellement dans ce même espace et centré.
/// Un calque absent donne simplement le média seul ; les sources ne sont jamais réécrites.
/// </summary>
public static class ImageComposer
{
    /// <summary>
    /// Compose le média et son calque. <paramref name="overlayPath"/> peut être nul, vide ou
    /// introuvable : le média est alors retourné seul. <paramref name="maxWidth"/> réduit le résultat
    /// sans jamais l'agrandir.
    /// </summary>
    public static SKBitmap ComposeToBitmap(string mainPath, string? overlayPath, int? maxWidth = null)
    {
        if (maxWidth is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxWidth),
                maxWidth,
                "La largeur maximale de la composition doit être strictement positive.");
        }

        using var main = ImageDecoder.Decode(mainPath);
        DecodedImage? overlay = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(overlayPath) && File.Exists(overlayPath))
            {
                overlay = ImageDecoder.Decode(overlayPath);
            }

            int mainWidth = main.DisplayWidth;
            int mainHeight = main.DisplayHeight;
            double outputScale = 1.0;
            if (maxWidth is { } limit && mainWidth > limit)
            {
                outputScale = limit / (double)mainWidth;
            }

            int outputWidth = Math.Max(1, (int)Math.Round(
                mainWidth * outputScale,
                MidpointRounding.AwayFromZero));
            int outputHeight = Math.Max(1, (int)Math.Round(
                mainHeight * outputScale,
                MidpointRounding.AwayFromZero));

            var target = new SKBitmap(new SKImageInfo(
                outputWidth,
                outputHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Premul));

            try
            {
                using var canvas = new SKCanvas(target);
                using var paint = new SKPaint { IsAntialias = true };
                var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(
                    main.Bitmap,
                    new SKRect(0, 0, outputWidth, outputHeight),
                    sampling,
                    paint);

                if (overlay is not null && overlay.DisplayWidth > 0 && overlay.DisplayHeight > 0)
                {
                    // Ajustement « contain » : le ratio du calque est conservé et le calque est centré.
                    double fit = Math.Min(
                        mainWidth / (double)overlay.DisplayWidth,
                        mainHeight / (double)overlay.DisplayHeight);
                    double drawnWidth = overlay.DisplayWidth * fit * outputScale;
                    double drawnHeight = overlay.DisplayHeight * fit * outputScale;
                    float left = (float)((outputWidth - drawnWidth) / 2.0);
                    float top = (float)((outputHeight - drawnHeight) / 2.0);

                    canvas.DrawBitmap(
                        overlay.Bitmap,
                        new SKRect(
                            left,
                            top,
                            (float)(left + drawnWidth),
                            (float)(top + drawnHeight)),
                        sampling,
                        paint);
                }

                canvas.Flush();
                return target;
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }
        finally
        {
            overlay?.Dispose();
        }
    }

    /// <summary>
    /// Compose puis encode le résultat dans <paramref name="format"/> (WebP par défaut). L'écriture
    /// est atomique et refusée si la destination désigne une des sources.
    /// </summary>
    public static void ComposeToFile(
        string mainPath,
        string? overlayPath,
        string destinationPath,
        SKEncodedImageFormat format = SKEncodedImageFormat.Webp,
        int? maxWidth = null)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Le chemin de destination est vide.", nameof(destinationPath));
        }

        if (format is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
        {
            throw new ArgumentException(
                $"Le format de sortie {format} n'est pas pris en charge. Utilisez JPEG, PNG ou WebP.",
                nameof(format));
        }

        EnsureDestinationIsNotSource(destinationPath, mainPath, nameof(mainPath));
        EnsureDestinationIsNotSource(destinationPath, overlayPath, nameof(overlayPath));

        int quality = format switch
        {
            SKEncodedImageFormat.Jpeg => 92,
            SKEncodedImageFormat.Webp => 90,
            _ => 100,
        };

        using var composed = ComposeToBitmap(mainPath, overlayPath, maxWidth);
        AtomicFile.WriteImage(composed, destinationPath, format, quality);
    }

    private static void EnsureDestinationIsNotSource(
        string destinationPath,
        string? sourcePath,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        string destinationFull;
        string sourceFull;
        try
        {
            destinationFull = Path.GetFullPath(destinationPath);
            sourceFull = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Un chemin invalide sera signalé par l'écriture ou la lecture, avec un message plus précis.
            return;
        }

        if (string.Equals(destinationFull, sourceFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"La composition ne peut pas remplacer la source « {sourcePath} ». Choisissez une destination différente.",
                parameterName);
        }
    }
}
