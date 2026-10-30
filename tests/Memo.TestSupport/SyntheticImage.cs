using SkiaSharp;

namespace Memo.TestSupport;

/// <summary>Formats d'image synthétique produits par <see cref="SyntheticImage"/>.</summary>
public enum SyntheticImageFormat
{
    Jpeg,
    Png,
    Webp,
}

/// <summary>
/// Génère des images déterministes avec SkiaSharp. Aucun média réel n'est lu :
/// le contenu dépend uniquement des paramètres et de la graine.
/// </summary>
public static class SyntheticImage
{
    /// <summary>Échelles de calque utilisées par le corpus (taille relative au média).</summary>
    public static readonly IReadOnlyList<double> OverlayScales = new[] { 0.89d, 1d, 1.04d, 1.73d };

    /// <summary>Dimensions usuelles d'une photo portrait synthétique.</summary>
    public const int DefaultPhotoWidth = 1080;

    /// <summary>Dimensions usuelles d'une photo portrait synthétique.</summary>
    public const int DefaultPhotoHeight = 1920;

    private static readonly string[] SecondaryCaptions =
    {
        "BON APREM",
        "JOUR CALME",
        "FLASHBACK",
        "ROUTE DU LAC",
        "FIN DE JOURNEE",
        "APRES LA PLUIE",
    };

    /// <summary>Crée une photo JPEG, PNG ou WebP opaque avec une date et un libellé lisibles.</summary>
    public static byte[] Photo(
        string date = "2021-07-14",
        string? caption = null,
        SyntheticImageFormat format = SyntheticImageFormat.Jpeg,
        int width = DefaultPhotoWidth,
        int height = DefaultPhotoHeight,
        int seed = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        caption ??= SecondaryCaptions[(int)(unchecked((uint)seed) % (uint)SecondaryCaptions.Length)];

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        DrawPhotoBackground(canvas, width, height, seed);
        DrawPhotoText(canvas, width, height, date, caption);

        return Encode(bitmap, format);
    }

    /// <summary>Crée une photo JPEG déterministe.</summary>
    public static byte[] Jpeg(string date = "2021-07-14", string? caption = null, int width = DefaultPhotoWidth, int height = DefaultPhotoHeight, int seed = 0) =>
        Photo(date, caption, SyntheticImageFormat.Jpeg, width, height, seed);

    /// <summary>Crée une photo PNG déterministe.</summary>
    public static byte[] Png(string date = "2021-07-14", string? caption = null, int width = DefaultPhotoWidth, int height = DefaultPhotoHeight, int seed = 0) =>
        Photo(date, caption, SyntheticImageFormat.Png, width, height, seed);

    /// <summary>Crée une photo WebP déterministe.</summary>
    public static byte[] Webp(string date = "2021-07-14", string? caption = null, int width = DefaultPhotoWidth, int height = DefaultPhotoHeight, int seed = 0) =>
        Photo(date, caption, SyntheticImageFormat.Webp, width, height, seed);

    /// <summary>
    /// Crée un calque RGBA transparent. La taille vaut <paramref name="scale"/> fois
    /// celle du média ; le contenu reste lisible même lorsque le calque est plus grand.
    /// </summary>
    public static byte[] Overlay(
        int mediaWidth,
        int mediaHeight,
        double scale = 1d,
        SyntheticImageFormat format = SyntheticImageFormat.Webp,
        string caption = "BON APREM",
        int seed = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);

        var width = Math.Max(1, (int)Math.Round(mediaWidth * scale, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(mediaHeight * scale, MidpointRounding.AwayFromZero));

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // Plaque semi-transparente : le calque doit rester identifiable comme RGBA.
        var plaque = new SKRect(width * 0.06f, height * 0.66f, width * 0.94f, height * 0.90f);
        using (var paint = new SKPaint
               {
                   Color = new SKColor(16, 22, 34, 190),
                   IsAntialias = false,
               })
        {
            canvas.DrawRoundRect(plaque, height * 0.025f, height * 0.025f, paint);
        }

        // Quelques marques translucides pour éviter un calque uniforme.
        using (var paint = new SKPaint { Color = new SKColor(255, 214, 102, 120), IsAntialias = false })
        {
            canvas.DrawCircle(width * 0.18f, height * 0.20f, Math.Min(width, height) * 0.07f, paint);
            canvas.DrawCircle(width * 0.82f, height * 0.14f, Math.Min(width, height) * 0.05f, paint);
        }

        var titleSize = Math.Max(8f, Math.Min(width, height) * 0.065f);
        DrawCentered(canvas, caption, width * 0.5f, height * 0.78f, titleSize, SKColors.White, bold: true);
        DrawCentered(canvas, "CALQUE SYNTHETIQUE", width * 0.5f, height * 0.845f, titleSize * 0.55f, new SKColor(235, 235, 245, 220), bold: false);

        return Encode(bitmap, format);
    }

    /// <summary>Crée un calque entièrement transparent, conservé comme donnée source.</summary>
    public static byte[] FullyTransparentOverlay(
        int mediaWidth,
        int mediaHeight,
        SyntheticImageFormat format = SyntheticImageFormat.Png)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaHeight);

        using var bitmap = new SKBitmap(new SKImageInfo(mediaWidth, mediaHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        return Encode(bitmap, format);
    }

    /// <summary>Lit les dimensions et le format réellement encodés, sans se fier au nom de fichier.</summary>
    public static (int Width, int Height, SyntheticImageFormat Format) ReadInfo(byte[] content)
    {
        using var data = SKData.CreateCopy(content);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Image non décodable.");
        var format = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => SyntheticImageFormat.Jpeg,
            SKEncodedImageFormat.Png => SyntheticImageFormat.Png,
            SKEncodedImageFormat.Webp => SyntheticImageFormat.Webp,
            _ => throw new InvalidDataException($"Format d'image inattendu : {codec.EncodedFormat}."),
        };
        return (codec.Info.Width, codec.Info.Height, format);
    }

    /// <summary>Signature binaire courte, utile pour diagnostiquer un format mal nommé.</summary>
    public static string ReadSignature(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return "jpeg";
        }

        if (content.Length >= 8 && content[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "png";
        }

        if (content.Length >= 12 &&
            content[..4].SequenceEqual("RIFF"u8) &&
            content.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "webp";
        }

        return "inconnu";
    }

    private static void DrawPhotoBackground(SKCanvas canvas, int width, int height, int seed)
    {
        var (top, bottom) = Palette(seed);

        using (var shader = SKShader.CreateLinearGradient(
                   new SKPoint(0, 0),
                   new SKPoint(width, height),
                   new[] { top, bottom },
                   new[] { 0f, 1f },
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(new SKRect(0, 0, width, height), paint);
        }

        // Formes déterministes : elles donnent un contenu visuel sans photographie réelle.
        var random = new Random(seed * 7919 + 17);
        for (var i = 0; i < 7; i++)
        {
            var radius = Math.Min(width, height) * (0.04f + (float)random.NextDouble() * 0.16f);
            var x = (float)random.NextDouble() * width;
            var y = (float)random.NextDouble() * height;
            var alpha = (byte)(22 + random.Next(54));
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, alpha),
                IsAntialias = false,
            };
            canvas.DrawCircle(x, y, radius, paint);
        }

        // Bandeau sombre pour garantir la lisibilité du texte.
        using (var paint = new SKPaint { Color = new SKColor(0, 0, 0, 110), IsAntialias = false })
        {
            canvas.DrawRect(new SKRect(0, height * 0.32f, width, height * 0.62f), paint);
        }
    }

    private static void DrawPhotoText(SKCanvas canvas, int width, int height, string date, string caption)
    {
        var baseSize = Math.Min(width, height) * 0.085f;
        DrawCentered(canvas, date, width * 0.5f, height * 0.43f, baseSize, SKColors.White, bold: true);
        DrawCentered(canvas, caption, width * 0.5f, height * 0.52f, baseSize * 0.82f, new SKColor(255, 232, 170), bold: true);
        DrawCentered(canvas, "IMAGE SYNTHETIQUE", width * 0.5f, height * 0.585f, baseSize * 0.38f, new SKColor(235, 235, 245, 220), bold: false);
    }

    private static void DrawCentered(SKCanvas canvas, string text, float centerX, float centerY, float size, SKColor color, bool bold)
    {
        // Le paramètre bold est conservé pour l'API des appels ; la police bitmap
        // n'a qu'une graisse afin de rester strictement déterministe.
        _ = bold;
        SyntheticBitmapFont.DrawCentered(canvas, text, centerX, centerY, size, color);
    }

    private static (SKColor Top, SKColor Bottom) Palette(int seed)
    {
        var index = (int)((uint)seed % 6u);
        return index switch
        {
            0 => (new SKColor(28, 58, 102), new SKColor(116, 176, 196)),
            1 => (new SKColor(84, 40, 96), new SKColor(226, 138, 120)),
            2 => (new SKColor(20, 74, 70), new SKColor(180, 202, 130)),
            3 => (new SKColor(92, 54, 30), new SKColor(232, 178, 110)),
            4 => (new SKColor(36, 36, 58), new SKColor(142, 150, 198)),
            _ => (new SKColor(16, 62, 48), new SKColor(98, 168, 148)),
        };
    }

    private static byte[] Encode(SKBitmap bitmap, SyntheticImageFormat format)
    {
        var encodedFormat = format switch
        {
            SyntheticImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            SyntheticImageFormat.Png => SKEncodedImageFormat.Png,
            SyntheticImageFormat.Webp => SKEncodedImageFormat.Webp,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Format inconnu."),
        };

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(encodedFormat, 90)
                         ?? throw new InvalidOperationException($"Encodage {format} impossible.");
        return data.ToArray();
    }
}
