using SkiaSharp;

namespace Memo.Media.Tests;

[TestClass]
public sealed class ImageComposerTests
{
    [TestMethod]
    public void Compose_MissingOverlay_ReturnsMainAlone()
    {
        using var directory = new TempDirectory();
        using var main = SyntheticImages.CreateSolid(120, 80, SKColors.CornflowerBlue);
        string mainPath = directory.File("main.png");
        SyntheticImages.Save(main, mainPath, SKEncodedImageFormat.Png);

        using SKBitmap composed = ImageComposer.ComposeToBitmap(
            mainPath,
            directory.File("calque-absent.png"));

        Assert.AreEqual(120, composed.Width);
        Assert.AreEqual(80, composed.Height);
        AssertColor(composed.GetPixel(60, 40), SKColors.CornflowerBlue, tolerance: 10);
    }

    [TestMethod]
    public void Compose_SquareOverlayOnWideMain_IsCenteredAndKeepsRatio()
    {
        using var directory = new TempDirectory();
        using var main = SyntheticImages.CreateSolid(200, 100, SKColors.Blue);
        using var overlay = SyntheticImages.CreateSolid(400, 400, SKColors.Red);
        string mainPath = directory.File("main.png");
        string overlayPath = directory.File("overlay.png");
        SyntheticImages.Save(main, mainPath, SKEncodedImageFormat.Png);
        SyntheticImages.Save(overlay, overlayPath, SKEncodedImageFormat.Png);

        using SKBitmap composed = ImageComposer.ComposeToBitmap(mainPath, overlayPath);

        Assert.AreEqual(200, composed.Width);
        Assert.AreEqual(100, composed.Height);
        AssertColor(composed.GetPixel(10, 50), SKColors.Blue, tolerance: 10, "marge gauche");
        AssertColor(composed.GetPixel(100, 50), SKColors.Red, tolerance: 10, "centre");
        AssertColor(composed.GetPixel(190, 50), SKColors.Blue, tolerance: 10, "marge droite");
    }

    [TestMethod]
    public void Compose_SmallOverlay_IsUpscaledToMediaSize()
    {
        using var directory = new TempDirectory();
        using var main = SyntheticImages.CreateSolid(300, 300, SKColors.Blue);
        using var overlay = SyntheticImages.CreateSolid(267, 267, SKColors.Red);
        string mainPath = directory.File("main.png");
        string overlayPath = directory.File("overlay-0.89.png");
        SyntheticImages.Save(main, mainPath, SKEncodedImageFormat.Png);
        SyntheticImages.Save(overlay, overlayPath, SKEncodedImageFormat.Png);

        using SKBitmap composed = ImageComposer.ComposeToBitmap(mainPath, overlayPath);

        Assert.AreEqual(300, composed.Width);
        Assert.AreEqual(300, composed.Height);
        AssertColor(composed.GetPixel(150, 150), SKColors.Red, tolerance: 10);
        AssertColor(composed.GetPixel(5, 5), SKColors.Red, tolerance: 10);
    }

    [TestMethod]
    public void Compose_LargeOverlay_IsDownscaledToMediaSize()
    {
        using var directory = new TempDirectory();
        using var main = SyntheticImages.CreateSolid(300, 300, SKColors.Blue);
        using var overlay = SyntheticImages.CreateSolid(519, 519, SKColors.Red);
        string mainPath = directory.File("main.png");
        string overlayPath = directory.File("overlay-1.73.png");
        SyntheticImages.Save(main, mainPath, SKEncodedImageFormat.Png);
        SyntheticImages.Save(overlay, overlayPath, SKEncodedImageFormat.Png);

        using SKBitmap composed = ImageComposer.ComposeToBitmap(mainPath, overlayPath);

        Assert.AreEqual(300, composed.Width);
        Assert.AreEqual(300, composed.Height);
        AssertColor(composed.GetPixel(150, 150), SKColors.Red, tolerance: 10);
        AssertColor(composed.GetPixel(5, 5), SKColors.Red, tolerance: 10);
    }

    [TestMethod]
    public void Compose_PreservesTransparencyOutsideOverlay()
    {
        using var directory = new TempDirectory();
        using var main = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(main))
        {
            canvas.Clear(SKColors.Transparent);
        }

        using var overlay = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(overlay))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(new SKRect(40, 40, 60, 60), paint);
        }

        string mainPath = directory.File("main-transparent.png");
        string overlayPath = directory.File("overlay-transparent.png");
        SyntheticImages.Save(main, mainPath, SKEncodedImageFormat.Png);
        SyntheticImages.Save(overlay, overlayPath, SKEncodedImageFormat.Png);

        using SKBitmap composed = ImageComposer.ComposeToBitmap(mainPath, overlayPath);

        Assert.AreEqual(0, composed.GetPixel(2, 2).Alpha);
        Assert.AreNotEqual(0, composed.GetPixel(50, 50).Alpha);
        AssertColor(composed.GetPixel(50, 50), SKColors.Red, tolerance: 10);
    }

    [TestMethod]
    public void Compose_MaxWidth_DownscalesWholeComposition()
    {
        using var directory = new TempDirectory();
        using var main = SyntheticImages.CreateSolid(1000, 500, SKColors.Blue);
        using var overlay = SyntheticImages.CreateSolid(1000, 500, SKColors.Red);
        string mainPath = directory.File("main.png");
        string overlayPath = directory.File("overlay.png");
        SyntheticImages.Save(main, mainPath, SKEncodedImageFormat.Png);
        SyntheticImages.Save(overlay, overlayPath, SKEncodedImageFormat.Png);

        using SKBitmap composed = ImageComposer.ComposeToBitmap(mainPath, overlayPath, maxWidth: 250);

        Assert.AreEqual(250, composed.Width);
        Assert.AreEqual(125, composed.Height);
        AssertColor(composed.GetPixel(125, 60), SKColors.Red, tolerance: 10);
    }

    [TestMethod]
    public void ComposeToFile_WritesWebPContentRegardlessOfExtension()
    {
        using var directory = new TempDirectory();
        using var main = SyntheticImages.CreateSolid(100, 80, SKColors.CornflowerBlue);
        using var overlay = SyntheticImages.CreateSolid(100, 80, SKColors.Red);
        string mainPath = directory.File("main.png");
        string overlayPath = directory.File("overlay.png");
        string destinationPath = directory.File("composition.bin");
        SyntheticImages.Save(main, mainPath, SKEncodedImageFormat.Png);
        SyntheticImages.Save(overlay, overlayPath, SKEncodedImageFormat.Png);

        ImageComposer.ComposeToFile(mainPath, overlayPath, destinationPath, SKEncodedImageFormat.Webp);

        MediaFormatInfo format = MediaFormatDetector.Detect(destinationPath);
        Assert.AreEqual(MediaFormatKind.WebP, format.Format);
        using var decoded = SKBitmap.Decode(destinationPath);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(100, decoded.Width);
        Assert.AreEqual(80, decoded.Height);
        AssertColor(decoded.GetPixel(50, 40), SKColors.Red, tolerance: 30);
    }

    [TestMethod]
    public void ComposeToFile_Png_PreservesAlpha()
    {
        using var directory = new TempDirectory();
        using var main = new SKBitmap(new SKImageInfo(50, 50, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(main))
        {
            canvas.Clear(SKColors.Transparent);
        }

        string mainPath = directory.File("main.png");
        string destinationPath = directory.File("composition.png");
        SyntheticImages.Save(main, mainPath, SKEncodedImageFormat.Png);

        ImageComposer.ComposeToFile(mainPath, null, destinationPath, SKEncodedImageFormat.Png);

        using var decoded = SKBitmap.Decode(destinationPath);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(0, decoded.GetPixel(10, 10).Alpha);
    }

    [TestMethod]
    public void ComposeToFile_RefusesToReplaceSource()
    {
        using var directory = new TempDirectory();
        using var main = SyntheticImages.CreateSolid(50, 50, SKColors.Blue);
        string mainPath = directory.File("main.png");
        SyntheticImages.Save(main, mainPath, SKEncodedImageFormat.Png);
        byte[] before = File.ReadAllBytes(mainPath);

        Assert.ThrowsExactly<ArgumentException>(
            () => ImageComposer.ComposeToFile(mainPath, null, mainPath, SKEncodedImageFormat.Png));

        CollectionAssert.AreEqual(before, File.ReadAllBytes(mainPath));
    }

    [TestMethod]
    public void ComposeToFile_UnsupportedFormat_Throws()
    {
        using var directory = new TempDirectory();
        using var main = SyntheticImages.CreateSolid(20, 20, SKColors.Blue);
        string mainPath = directory.File("main.png");
        SyntheticImages.Save(main, mainPath, SKEncodedImageFormat.Png);

        Assert.ThrowsExactly<ArgumentException>(
            () => ImageComposer.ComposeToFile(
                mainPath,
                null,
                directory.File("sortie.bmp"),
                SKEncodedImageFormat.Bmp));
    }

    private static void AssertColor(SKColor actual, SKColor expected, int tolerance, string? context = null)
    {
        bool ok =
            Math.Abs(actual.Red - expected.Red) <= tolerance &&
            Math.Abs(actual.Green - expected.Green) <= tolerance &&
            Math.Abs(actual.Blue - expected.Blue) <= tolerance;
        Assert.IsTrue(ok, $"{(context is null ? "Couleur" : context)} attendue {expected}, obtenue {actual}.");
    }
}
