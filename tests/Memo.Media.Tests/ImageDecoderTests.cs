using Memo.Core;
using SkiaSharp;

namespace Memo.Media.Tests;

[TestClass]
public sealed class ImageDecoderTests
{
    [TestMethod]
    public void Decode_Png_PreservesDimensionsAndAlpha()
    {
        using var directory = new TempDirectory();
        using var source = new SKBitmap(new SKImageInfo(24, 16, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(255, 0, 0, 128) };
            canvas.DrawRect(new SKRect(4, 4, 20, 12), paint);
        }

        string path = directory.File("transparent.png");
        SyntheticImages.Save(source, path, SKEncodedImageFormat.Png);

        using DecodedImage decoded = ImageDecoder.Decode(path);

        Assert.AreEqual(24, decoded.EncodedWidth);
        Assert.AreEqual(16, decoded.EncodedHeight);
        Assert.AreEqual(24, decoded.DisplayWidth);
        Assert.AreEqual(16, decoded.DisplayHeight);
        Assert.IsTrue(decoded.HasAlpha);
        Assert.AreEqual(SKEncodedOrigin.TopLeft, decoded.Origin);
        Assert.AreEqual(0, decoded.Rotation);
        Assert.AreEqual("png", decoded.Format);
        Assert.AreEqual(0, decoded.Bitmap.GetPixel(0, 0).Alpha);
        Assert.AreNotEqual(0, decoded.Bitmap.GetPixel(12, 8).Alpha);
    }

    [TestMethod]
    public void Decode_Jpeg_ReportsOpaqueDimensions()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(40, 30, SKColors.CornflowerBlue);
        string path = directory.File("photo.jpg");
        SyntheticImages.Save(source, path, SKEncodedImageFormat.Jpeg);

        using DecodedImage decoded = ImageDecoder.Decode(path);

        Assert.AreEqual(40, decoded.DisplayWidth);
        Assert.AreEqual(30, decoded.DisplayHeight);
        Assert.IsFalse(decoded.HasAlpha);
        AssertColor(decoded.Bitmap.GetPixel(20, 15), SKColors.CornflowerBlue, tolerance: 30);
    }

    [TestMethod]
    public void Decode_WebP_ContentWithPngExtension_UsesContent()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(32, 20, SKColors.SeaGreen);
        string path = directory.File("trompeur.png");
        SyntheticImages.Save(source, path, SKEncodedImageFormat.Webp);

        using DecodedImage decoded = ImageDecoder.Decode(path);

        Assert.AreEqual(32, decoded.DisplayWidth);
        Assert.AreEqual(20, decoded.DisplayHeight);
        Assert.AreEqual("webp", decoded.Format);
        AssertColor(decoded.Bitmap.GetPixel(16, 10), SKColors.SeaGreen, tolerance: 40);
    }

    [TestMethod]
    public void Decode_JpegWithExifOrientation6_AppliesRotationOnce()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateQuadrants(80, 60);
        string path = directory.File("portrait-exif6.jpg");
        SyntheticImages.SaveJpegWithOrientation(source, path, orientation: 6);

        using DecodedImage decoded = ImageDecoder.Decode(path);

        Assert.AreEqual(80, decoded.EncodedWidth);
        Assert.AreEqual(60, decoded.EncodedHeight);
        Assert.AreEqual(60, decoded.DisplayWidth);
        Assert.AreEqual(80, decoded.DisplayHeight);
        Assert.AreEqual(SKEncodedOrigin.RightTop, decoded.Origin);
        Assert.AreEqual(90, decoded.Rotation);

        // Après rotation 90° horaire : haut-gauche = bleu, haut-droit = rouge,
        // bas-gauche = jaune, bas-droit = vert.
        AssertDominant(decoded.Bitmap.GetPixel(15, 20), "bleu");
        AssertDominant(decoded.Bitmap.GetPixel(45, 20), "rouge");
        AssertDominant(decoded.Bitmap.GetPixel(15, 60), "jaune");
        AssertDominant(decoded.Bitmap.GetPixel(45, 60), "vert");
    }

    [TestMethod]
    public void Decode_Bytes_MemoryInputWorks()
    {
        using var source = SyntheticImages.CreateSolid(12, 7, SKColors.Goldenrod);
        byte[] png = SyntheticImages.Encode(source, SKEncodedImageFormat.Png);

        using DecodedImage decoded = ImageDecoder.Decode(png, "en-memoire.png");

        Assert.AreEqual(12, decoded.DisplayWidth);
        Assert.AreEqual(7, decoded.DisplayHeight);
    }

    [TestMethod]
    public void Decode_MissingFile_ThrowsTypedFrenchException()
    {
        using var directory = new TempDirectory();
        string path = directory.File("absent.png");

        MediaDecodeException exception = Assert.ThrowsExactly<MediaDecodeException>(
            () => ImageDecoder.Decode(path));

        Assert.AreEqual("fichier-introuvable", exception.Code);
        StringAssert.Contains(exception.Message, "introuvable");
    }

    [TestMethod]
    public void Decode_EmptyBytes_ThrowsTypedException()
    {
        MediaDecodeException exception = Assert.ThrowsExactly<MediaDecodeException>(
            () => ImageDecoder.Decode(ReadOnlySpan<byte>.Empty));

        Assert.AreEqual("donnees-vides", exception.Code);
    }

    [TestMethod]
    public void Decode_CorruptJpeg_ThrowsTypedException()
    {
        using var directory = new TempDirectory();
        string path = directory.File("corrompu.jpg");
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 1, 2, 3, 4, 5, 6 });

        MediaDecodeException exception = Assert.ThrowsExactly<MediaDecodeException>(
            () => ImageDecoder.Decode(path));

        Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Code));
        StringAssert.Contains(exception.Message, "fichier");
    }

    [TestMethod]
    public void Decode_TruncatedPng_ThrowsTypedExceptionWithoutCrash()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(120, 90, SKColors.CadetBlue);
        byte[] png = SyntheticImages.Encode(source, SKEncodedImageFormat.Png);
        string path = directory.File("tronquee.png");
        File.WriteAllBytes(path, png.AsSpan(0, png.Length / 2).ToArray());

        MediaDecodeException exception = Assert.ThrowsExactly<MediaDecodeException>(
            () => ImageDecoder.Decode(path));

        Assert.IsTrue(
            exception.Code is "image-incomplete" or "image-corrompue" or "decodage-impossible",
            $"Code inattendu : {exception.Code}");
        StringAssert.Contains(exception.Message, "fichier");
    }

    [TestMethod]
    public void Decode_LockedFile_ThrowsTypedException()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(64, 48, SKColors.SlateBlue);
        string path = directory.File("verrouillee.png");
        SyntheticImages.Save(source, path, SKEncodedImageFormat.Png);

        using var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        MediaDecodeException exception = Assert.ThrowsExactly<MediaDecodeException>(
            () => ImageDecoder.Decode(path));

        Assert.AreEqual("fichier-illisible", exception.Code);
        StringAssert.Contains(exception.Message, "verrouillé");
    }

    [TestMethod]
    public void Decode_VideoBytes_RejectsAsImage()
    {
        byte[] mp4 = BuildFtyp("isom", "isom", "mp42");

        MediaDecodeException exception = Assert.ThrowsExactly<MediaDecodeException>(
            () => ImageDecoder.Decode(mp4, "video.mp4"));

        Assert.AreEqual("format-video", exception.Code);
        StringAssert.Contains(exception.Message, "vidéo");
    }

    [TestMethod]
    public void Decode_Heic_IsRecognizedButRejectedWithClearMessage()
    {
        byte[] heic = BuildFtyp("heic", "mif1", "heic");

        MediaDecodeException exception = Assert.ThrowsExactly<MediaDecodeException>(
            () => ImageDecoder.Decode(heic, "photo.heic"));

        Assert.AreEqual("format-non-decode", exception.Code);
        StringAssert.Contains(exception.Message, "HEIC");
    }

    private static byte[] BuildFtyp(string majorBrand, params string[] compatibleBrands)
    {
        int size = 16 + (compatibleBrands.Length * 4);
        var data = new byte[size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), (uint)size);
        System.Text.Encoding.ASCII.GetBytes("ftyp").CopyTo(data, 4);
        System.Text.Encoding.ASCII.GetBytes(majorBrand).CopyTo(data, 8);
        for (int index = 0; index < compatibleBrands.Length; index++)
        {
            System.Text.Encoding.ASCII.GetBytes(compatibleBrands[index]).CopyTo(data, 16 + (index * 4));
        }

        return data;
    }

    private static void AssertColor(SKColor actual, SKColor expected, int tolerance)
    {
        Assert.IsTrue(
            Math.Abs(actual.Red - expected.Red) <= tolerance &&
            Math.Abs(actual.Green - expected.Green) <= tolerance &&
            Math.Abs(actual.Blue - expected.Blue) <= tolerance,
            $"Couleur attendue {expected}, obtenue {actual}.");
    }

    private static void AssertDominant(SKColor color, string expected)
    {
        bool ok = expected switch
        {
            "rouge" => color.Red > 180 && color.Green < 90 && color.Blue < 90,
            "vert" => color.Green > 150 && color.Red < 110 && color.Blue < 110,
            "bleu" => color.Blue > 180 && color.Red < 90 && color.Green < 90,
            "jaune" => color.Red > 180 && color.Green > 180 && color.Blue < 90,
            _ => false,
        };

        Assert.IsTrue(ok, $"Couleur {expected} attendue, obtenue {color}.");
    }
}
