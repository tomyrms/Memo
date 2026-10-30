using SkiaSharp;

namespace Memo.Media.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(TestWorkspace.Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class TestWorkspace
{
    public static string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "memo-media-tests",
        Environment.ProcessId.ToString());

    static TestWorkspace()
    {
        Directory.CreateDirectory(Root);
    }

    public static string SharedDirectory(string name)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}

[TestClass]
public sealed class TestWorkspaceCleanup
{
    [AssemblyCleanup]
    public static void Cleanup()
    {
        try
        {
            if (Directory.Exists(TestWorkspace.Root))
            {
                Directory.Delete(TestWorkspace.Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class SyntheticImages
{
    public static SKBitmap CreateSolid(int width, int height, SKColor color, bool transparent = false)
    {
        var info = new SKImageInfo(
            width,
            height,
            SKColorType.Bgra8888,
            transparent ? SKAlphaType.Premul : SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(transparent ? SKColors.Transparent : color);
        return bitmap;
    }

    public static SKBitmap CreateQuadrants(int width = 80, int height = 60)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { IsAntialias = false };
        int halfWidth = width / 2;
        int halfHeight = height / 2;

        paint.Color = SKColors.Red;
        canvas.DrawRect(new SKRect(0, 0, halfWidth, halfHeight), paint);
        paint.Color = SKColors.Lime;
        canvas.DrawRect(new SKRect(halfWidth, 0, width, halfHeight), paint);
        paint.Color = SKColors.Blue;
        canvas.DrawRect(new SKRect(0, halfHeight, halfWidth, height), paint);
        paint.Color = SKColors.Yellow;
        canvas.DrawRect(new SKRect(halfWidth, halfHeight, width, height), paint);
        canvas.Flush();
        return bitmap;
    }

    public static void Save(SKBitmap bitmap, string path, SKEncodedImageFormat format, int quality = 95)
    {
        using var data = bitmap.Encode(format, quality)
            ?? throw new InvalidOperationException($"Encodage {format} impossible.");
        File.WriteAllBytes(path, data.ToArray());
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 95)
    {
        using var data = bitmap.Encode(format, quality)
            ?? throw new InvalidOperationException($"Encodage {format} impossible.");
        return data.ToArray();
    }

    /// <summary>
    /// Insère un bloc APP1 EXIF minimal avec le tag 274. SkiaSharp n'écrit pas d'EXIF ;
    /// ce bloc permet de vérifier l'application de l'orientation sur un vrai JPEG.
    /// </summary>
    public static byte[] InsertJpegExifOrientation(byte[] jpeg, ushort orientation)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new ArgumentException("Le tableau ne commence pas par la signature JPEG.", nameof(jpeg));
        }

        if (orientation is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(orientation));
        }

        // TIFF little-endian : en-tête, IFD0 avec une seule entrée (0x0112, SHORT, 1, orientation).
        byte[] tiff =
        {
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,
            (byte)orientation, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };

        byte[] payload = new byte[6 + tiff.Length];
        "Exif\0\0"u8.CopyTo(payload);
        tiff.CopyTo(payload, 6);

        int segmentLength = payload.Length + 2;
        var result = new byte[jpeg.Length + payload.Length + 4];

        result[0] = 0xFF;
        result[1] = 0xD8;
        result[2] = 0xFF;
        result[3] = 0xE1;
        result[4] = (byte)(segmentLength >> 8);
        result[5] = (byte)segmentLength;
        payload.CopyTo(result, 6);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(6 + payload.Length));

        return result;
    }

    public static string SaveJpegWithOrientation(
        SKBitmap bitmap,
        string path,
        ushort orientation,
        int quality = 95)
    {
        byte[] jpeg = Encode(bitmap, SKEncodedImageFormat.Jpeg, quality);
        File.WriteAllBytes(path, InsertJpegExifOrientation(jpeg, orientation));
        return path;
    }
}
