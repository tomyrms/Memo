using System.Security.Cryptography;
using SkiaSharp;

namespace Memo.Media.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ThumbnailServiceTests
{
    [TestMethod]
    public void CreateImageThumbnail_LargeImage_FitsMaxSizeAndKeepsRatio()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(800, 400, SKColors.CornflowerBlue);
        string sourcePath = directory.File("grande.png");
        string destinationPath = directory.File("miniature.jpg");
        SyntheticImages.Save(source, sourcePath, SKEncodedImageFormat.Png);

        bool created = ThumbnailService.CreateImageThumbnail(
            sourcePath,
            destinationPath,
            maxSize: 512,
            out string? diagnostic);

        Assert.IsTrue(created, diagnostic);
        Assert.IsNull(diagnostic);
        Assert.IsTrue(File.Exists(destinationPath));

        using var thumbnail = SKBitmap.Decode(destinationPath);
        Assert.IsNotNull(thumbnail);
        Assert.AreEqual(512, thumbnail.Width);
        Assert.AreEqual(256, thumbnail.Height);
    }

    [TestMethod]
    public void CreateImageThumbnail_SmallImage_IsNotUpscaled()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(100, 80, SKColors.SeaGreen);
        string sourcePath = directory.File("petite.png");
        string destinationPath = directory.File("petite-miniature.jpg");
        SyntheticImages.Save(source, sourcePath, SKEncodedImageFormat.Png);

        Assert.IsTrue(ThumbnailService.CreateImageThumbnail(sourcePath, destinationPath, 512, out string? diagnostic), diagnostic);

        using var thumbnail = SKBitmap.Decode(destinationPath);
        Assert.IsNotNull(thumbnail);
        Assert.AreEqual(100, thumbnail.Width);
        Assert.AreEqual(80, thumbnail.Height);
    }

    [TestMethod]
    public void CreateImageThumbnail_TransparentPng_UsesNeutralBackground()
    {
        using var directory = new TempDirectory();
        using var source = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(new SKRect(40, 40, 60, 60), paint);
        }

        string sourcePath = directory.File("transparent.png");
        string destinationPath = directory.File("transparent.jpg");
        SyntheticImages.Save(source, sourcePath, SKEncodedImageFormat.Png);

        Assert.IsTrue(ThumbnailService.CreateImageThumbnail(sourcePath, destinationPath, 512, out string? diagnostic), diagnostic);

        using var thumbnail = SKBitmap.Decode(destinationPath);
        Assert.IsNotNull(thumbnail);
        SKColor corner = thumbnail.GetPixel(2, 2);
        Assert.IsTrue(corner.Red > 200 && corner.Green > 200 && corner.Blue > 200, $"Fond neutre attendu, obtenu {corner}.");
        SKColor center = thumbnail.GetPixel(50, 50);
        Assert.IsTrue(center.Red > 180 && center.Green < 90 && center.Blue < 90, $"Rouge attendu au centre, obtenu {center}.");
    }

    [TestMethod]
    public void CreateImageThumbnail_DoesNotModifySource()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(200, 100, SKColors.Goldenrod);
        string sourcePath = directory.File("source.png");
        string destinationPath = directory.File("derivee.jpg");
        SyntheticImages.Save(source, sourcePath, SKEncodedImageFormat.Png);
        byte[] before = SHA256.HashData(File.ReadAllBytes(sourcePath));

        Assert.IsTrue(ThumbnailService.CreateImageThumbnail(sourcePath, destinationPath, 512, out string? diagnostic), diagnostic);

        byte[] after = SHA256.HashData(File.ReadAllBytes(sourcePath));
        CollectionAssert.AreEqual(before, after);
        Assert.IsFalse(Directory.EnumerateFiles(directory.Root, "*.tmp").Any());
    }

    [TestMethod]
    public void CreateImageThumbnail_MissingSource_ReturnsFalseWithDiagnostic()
    {
        using var directory = new TempDirectory();

        bool created = ThumbnailService.CreateImageThumbnail(
            directory.File("absent.png"),
            directory.File("miniature.jpg"),
            512,
            out string? diagnostic);

        Assert.IsFalse(created);
        Assert.IsFalse(string.IsNullOrWhiteSpace(diagnostic));
        StringAssert.Contains(diagnostic, "introuvable");
        Assert.IsFalse(File.Exists(directory.File("miniature.jpg")));
    }

    [TestMethod]
    public void CreateImageThumbnail_DestinationEqualsSource_RefusesAndKeepsSource()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(60, 40, SKColors.Purple);
        string path = directory.File("source.png");
        SyntheticImages.Save(source, path, SKEncodedImageFormat.Png);
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));

        bool created = ThumbnailService.CreateImageThumbnail(path, path, 512, out string? diagnostic);

        Assert.IsFalse(created);
        StringAssert.Contains(diagnostic, "remplacer la source");
        CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [TestMethod]
    public void CreateImageThumbnail_CreatesDestinationDirectory()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(30, 20, SKColors.Teal);
        string sourcePath = directory.File("source.png");
        string destinationPath = Path.Combine(directory.Root, "sous-dossier", "miniature.jpg");
        SyntheticImages.Save(source, sourcePath, SKEncodedImageFormat.Png);

        Assert.IsTrue(ThumbnailService.CreateImageThumbnail(sourcePath, destinationPath, 512, out string? diagnostic), diagnostic);
        Assert.IsTrue(File.Exists(destinationPath));
    }

    [TestMethod]
    public void CreateImageThumbnail_LockedDestination_ReturnsFalseWithoutTouchingSource()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(80, 60, SKColors.Olive);
        string sourcePath = directory.File("source.png");
        string destinationPath = directory.File("verrouillee.jpg");
        SyntheticImages.Save(source, sourcePath, SKEncodedImageFormat.Png);
        byte[] before = SHA256.HashData(File.ReadAllBytes(sourcePath));

        using (var destinationLock = new FileStream(
                   destinationPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            bool created = ThumbnailService.CreateImageThumbnail(
                sourcePath,
                destinationPath,
                512,
                out string? diagnostic);

            Assert.IsFalse(created);
            Assert.IsFalse(string.IsNullOrWhiteSpace(diagnostic));
        }

        CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(sourcePath)));
        Assert.IsFalse(Directory.EnumerateFiles(directory.Root, "*.tmp").Any());
    }

    [TestMethod]
    public void CreateImageThumbnail_InvalidMaxSize_ReturnsFalse()
    {
        using var directory = new TempDirectory();
        using var source = SyntheticImages.CreateSolid(30, 20, SKColors.Teal);
        string sourcePath = directory.File("source.png");
        SyntheticImages.Save(source, sourcePath, SKEncodedImageFormat.Png);

        bool created = ThumbnailService.CreateImageThumbnail(sourcePath, directory.File("x.jpg"), 0, out string? diagnostic);

        Assert.IsFalse(created);
        StringAssert.Contains(diagnostic, "strictement positive");
    }

    [TestMethod]
    public void CreateVideoThumbnail_H264_ProducesExpectedFrame()
    {
        string videoPath = VideoFixtures.H264Path;
        using var directory = new TempDirectory();
        string destinationPath = directory.File("video.jpg");

        bool created = ThumbnailService.CreateVideoThumbnail(
            videoPath,
            destinationPath,
            512,
            TimeSpan.FromSeconds(0.4),
            out string? diagnostic);

        Assert.IsTrue(created, diagnostic);
        using var thumbnail = SKBitmap.Decode(destinationPath);
        Assert.IsNotNull(thumbnail);
        Assert.AreEqual(320, thumbnail.Width);
        Assert.AreEqual(240, thumbnail.Height);
        SKColor color = thumbnail.GetPixel(160, 120);
        Assert.IsTrue(color.Red > 180 && color.Green < 90 && color.Blue < 90, $"Rouge attendu, obtenu {color}.");
    }

    [TestMethod]
    public void CreateVideoThumbnail_Hevc_ProducesExpectedFrame()
    {
        string videoPath = VideoFixtures.HevcPath;
        using var directory = new TempDirectory();
        string destinationPath = directory.File("hevc.jpg");

        bool created = ThumbnailService.CreateVideoThumbnail(
            videoPath,
            destinationPath,
            512,
            TimeSpan.FromSeconds(0.4),
            out string? diagnostic);

        Assert.IsTrue(created, diagnostic);
        using var thumbnail = SKBitmap.Decode(destinationPath);
        Assert.IsNotNull(thumbnail);
        Assert.AreEqual(320, thumbnail.Width);
        Assert.AreEqual(240, thumbnail.Height);
        SKColor color = thumbnail.GetPixel(160, 120);
        Assert.IsTrue(color.Blue > 150 && color.Red < 100 && color.Green < 100, $"Bleu attendu, obtenu {color}.");
    }

    [TestMethod]
    public void CreateVideoThumbnail_MissingSource_ReturnsFalseWithDiagnostic()
    {
        using var directory = new TempDirectory();

        bool created = ThumbnailService.CreateVideoThumbnail(
            directory.File("absent.mp4"),
            directory.File("miniature.jpg"),
            512,
            TimeSpan.FromSeconds(0.4),
            out string? diagnostic);

        Assert.IsFalse(created);
        Assert.IsFalse(string.IsNullOrWhiteSpace(diagnostic));
        StringAssert.Contains(diagnostic, "introuvable");
    }

    [TestMethod]
    public void CreateVideoThumbnail_NegativePosition_ReturnsFalse()
    {
        string videoPath = VideoFixtures.H264Path;
        using var directory = new TempDirectory();

        bool created = ThumbnailService.CreateVideoThumbnail(
            videoPath,
            directory.File("miniature.jpg"),
            512,
            TimeSpan.FromSeconds(-1),
            out string? diagnostic);

        Assert.IsFalse(created);
        StringAssert.Contains(diagnostic, "négative");
    }
}
