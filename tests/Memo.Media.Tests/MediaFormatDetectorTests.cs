using System.Buffers.Binary;
using System.Text;
using Memo.Core;
using SkiaSharp;

namespace Memo.Media.Tests;

[TestClass]
public sealed class MediaFormatDetectorTests
{
    [TestMethod]
    public void Detect_Jpeg_BySignature()
    {
        using var directory = new TempDirectory();
        using var bitmap = SyntheticImages.CreateSolid(16, 12, SKColors.Orange);
        string path = directory.File("photo.jpg");
        SyntheticImages.Save(bitmap, path, SKEncodedImageFormat.Jpeg);

        MediaFormatInfo format = MediaFormatDetector.Detect(path);

        Assert.AreEqual(MediaFormatKind.Jpeg, format.Format);
        Assert.AreEqual(".jpg", format.Extension);
        Assert.AreEqual(MediaKind.Photo, format.MediaKind);
    }

    [TestMethod]
    public void Detect_Png_BySignature()
    {
        using var directory = new TempDirectory();
        using var bitmap = SyntheticImages.CreateSolid(16, 12, SKColors.Orange);
        string path = directory.File("photo.png");
        SyntheticImages.Save(bitmap, path, SKEncodedImageFormat.Png);

        MediaFormatInfo format = MediaFormatDetector.Detect(path);

        Assert.AreEqual(MediaFormatKind.Png, format.Format);
        Assert.AreEqual(".png", format.Extension);
    }

    [TestMethod]
    public void Detect_WebP_BySignature()
    {
        using var directory = new TempDirectory();
        using var bitmap = SyntheticImages.CreateSolid(16, 12, SKColors.Orange);
        string path = directory.File("photo.webp");
        SyntheticImages.Save(bitmap, path, SKEncodedImageFormat.Webp);

        MediaFormatInfo format = MediaFormatDetector.Detect(path);

        Assert.AreEqual(MediaFormatKind.WebP, format.Format);
        Assert.AreEqual(".webp", format.Extension);
    }

    [TestMethod]
    public void Detect_WebP_ContentWithPngName_ReportsRealFormat()
    {
        using var directory = new TempDirectory();
        using var bitmap = SyntheticImages.CreateSolid(16, 12, SKColors.Orange);
        string path = directory.File("trompeur.png");
        SyntheticImages.Save(bitmap, path, SKEncodedImageFormat.Webp);

        MediaFormatInfo format = MediaFormatDetector.Detect(path);

        Assert.AreEqual(MediaFormatKind.WebP, format.Format);
        Assert.AreEqual(".webp", format.Extension);
        Assert.AreEqual(MediaKind.Photo, format.MediaKind);
    }

    [TestMethod]
    public void Detect_Mp4_FromFtyp()
    {
        MediaFormatInfo format = MediaFormatDetector.Detect(BuildFtyp("isom", "isom", "iso2", "mp41"));

        Assert.AreEqual(MediaFormatKind.Mp4, format.Format);
        Assert.AreEqual(".mp4", format.Extension);
        Assert.AreEqual(MediaKind.Video, format.MediaKind);
    }

    [TestMethod]
    public void Detect_Mov_FromFtyp()
    {
        MediaFormatInfo format = MediaFormatDetector.Detect(BuildFtyp("qt  ", "qt  "));

        Assert.AreEqual(MediaFormatKind.Mov, format.Format);
        Assert.AreEqual(".mov", format.Extension);
    }

    [TestMethod]
    public void Detect_Heic_FromFtyp()
    {
        MediaFormatInfo format = MediaFormatDetector.Detect(BuildFtyp("heic", "mif1", "heic"));

        Assert.AreEqual(MediaFormatKind.Heic, format.Format);
        Assert.AreEqual(".heic", format.Extension);
        Assert.AreEqual(MediaKind.Photo, format.MediaKind);
    }

    [TestMethod]
    public void Detect_SkipsFreeBoxBeforeFtyp()
    {
        byte[] ftyp = BuildFtyp("mp42", "isom", "mp42");
        var header = new byte[8 + ftyp.Length];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 8);
        Encoding.ASCII.GetBytes("free").CopyTo(header, 4);
        ftyp.CopyTo(header, 8);

        MediaFormatInfo format = MediaFormatDetector.Detect(header);

        Assert.AreEqual(MediaFormatKind.Mp4, format.Format);
    }

    [TestMethod]
    public void Detect_Unknown_ReturnsUnknown()
    {
        MediaFormatInfo format = MediaFormatDetector.Detect(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });

        Assert.AreEqual(MediaFormatKind.Unknown, format.Format);
        Assert.AreEqual(MediaKind.Unknown, format.MediaKind);
        Assert.IsFalse(format.IsKnown);
        Assert.IsFalse(MediaFormatDetector.TryDetect(new byte[] { 1, 2, 3 }, out _));
    }

    [TestMethod]
    public void Detect_RealMp4File_FromGeneratedFixture()
    {
        MediaFormatInfo format = MediaFormatDetector.Detect(VideoFixtures.H264Path);

        Assert.AreEqual(MediaFormatKind.Mp4, format.Format);
        Assert.AreEqual(".mp4", format.Extension);
        Assert.AreEqual(MediaKind.Video, format.MediaKind);
    }

    [TestMethod]
    public void Detect_MissingFile_ThrowsTypedException()
    {
        using var directory = new TempDirectory();
        string path = directory.File("absent.jpg");

        MediaDecodeException exception = Assert.ThrowsExactly<MediaDecodeException>(
            () => MediaFormatDetector.Detect(path));

        Assert.AreEqual("fichier-introuvable", exception.Code);
        StringAssert.Contains(exception.Message, "introuvable");
        Assert.AreEqual(path, exception.SourcePath);
    }

    [TestMethod]
    public void Detect_Stream_RestoresPosition()
    {
        using var bitmap = SyntheticImages.CreateSolid(8, 8, SKColors.Purple);
        byte[] png = SyntheticImages.Encode(bitmap, SKEncodedImageFormat.Png);
        using var stream = new MemoryStream(new byte[] { 9, 9, 9, 9 }.Concat(png).ToArray());
        stream.Position = 4;

        MediaFormatInfo format = MediaFormatDetector.Detect(stream);

        Assert.AreEqual(MediaFormatKind.Png, format.Format);
        Assert.AreEqual(4, stream.Position);
    }

    private static byte[] BuildFtyp(string majorBrand, params string[] compatibleBrands)
    {
        int size = 16 + (compatibleBrands.Length * 4);
        var data = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), (uint)size);
        Encoding.ASCII.GetBytes("ftyp").CopyTo(data, 4);
        Encoding.ASCII.GetBytes(majorBrand).CopyTo(data, 8);
        for (int index = 0; index < compatibleBrands.Length; index++)
        {
            Encoding.ASCII.GetBytes(compatibleBrands[index]).CopyTo(data, 16 + (index * 4));
        }

        return data;
    }
}
