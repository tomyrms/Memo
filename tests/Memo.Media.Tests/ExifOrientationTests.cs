using SkiaSharp;

namespace Memo.Media.Tests;

[TestClass]
public sealed class ExifOrientationTests
{
    [TestMethod]
    public void Apply_MapsEveryOrientationToDisplayPixels()
    {
        // Pixel rouge unique en (1,1) dans une source 8×6, sur fond noir opaque.
        var cases = new (SKEncodedOrigin Origin, int Width, int Height, int X, int Y)[]
        {
            (SKEncodedOrigin.TopLeft, 8, 6, 1, 1),
            (SKEncodedOrigin.TopRight, 8, 6, 6, 1),
            (SKEncodedOrigin.BottomRight, 8, 6, 6, 4),
            (SKEncodedOrigin.BottomLeft, 8, 6, 1, 4),
            (SKEncodedOrigin.LeftTop, 6, 8, 1, 1),
            (SKEncodedOrigin.RightTop, 6, 8, 4, 1),
            (SKEncodedOrigin.RightBottom, 6, 8, 4, 6),
            (SKEncodedOrigin.LeftBottom, 6, 8, 1, 6),
        };

        foreach (var testCase in cases)
        {
            using var source = CreateMarker();
            SKBitmap result = ExifOrientation.Apply(source, testCase.Origin);
            try
            {
                Assert.AreEqual(testCase.Width, result.Width, $"Largeur pour {testCase.Origin}");
                Assert.AreEqual(testCase.Height, result.Height, $"Hauteur pour {testCase.Origin}");
                Assert.IsTrue(
                    IsRed(result.GetPixel(testCase.X, testCase.Y)),
                    $"Le pixel rouge devrait être en ({testCase.X},{testCase.Y}) pour {testCase.Origin}.");

                int redCount = 0;
                for (int y = 0; y < result.Height; y++)
                {
                    for (int x = 0; x < result.Width; x++)
                    {
                        if (IsRed(result.GetPixel(x, y)))
                        {
                            redCount++;
                        }
                    }
                }

                Assert.AreEqual(1, redCount, $"Un seul pixel rouge attendu pour {testCase.Origin}.");
            }
            finally
            {
                if (!ReferenceEquals(result, source))
                {
                    result.Dispose();
                }
            }
        }
    }

    [TestMethod]
    public void Apply_TopLeft_ReturnsSameBitmapWithoutCopy()
    {
        using var source = CreateMarker();

        SKBitmap result = ExifOrientation.Apply(source, SKEncodedOrigin.TopLeft);

        Assert.AreSame(source, result);
    }

    [TestMethod]
    public void Apply_InvalidSource_Throws()
    {
        using var empty = new SKBitmap();

        Assert.ThrowsExactly<ArgumentException>(() => ExifOrientation.Apply(empty, SKEncodedOrigin.RightTop));
    }

    [TestMethod]
    public void RotationAndSwapHelpers_MatchExifMatrix()
    {
        Assert.AreEqual(0, ExifOrientation.ToRotationDegrees(SKEncodedOrigin.TopLeft));
        Assert.AreEqual(0, ExifOrientation.ToRotationDegrees(SKEncodedOrigin.TopRight));
        Assert.AreEqual(180, ExifOrientation.ToRotationDegrees(SKEncodedOrigin.BottomRight));
        Assert.AreEqual(180, ExifOrientation.ToRotationDegrees(SKEncodedOrigin.BottomLeft));
        Assert.AreEqual(90, ExifOrientation.ToRotationDegrees(SKEncodedOrigin.LeftTop));
        Assert.AreEqual(90, ExifOrientation.ToRotationDegrees(SKEncodedOrigin.RightTop));
        Assert.AreEqual(270, ExifOrientation.ToRotationDegrees(SKEncodedOrigin.RightBottom));
        Assert.AreEqual(270, ExifOrientation.ToRotationDegrees(SKEncodedOrigin.LeftBottom));

        Assert.IsFalse(ExifOrientation.SwapsDimensions(SKEncodedOrigin.TopLeft));
        Assert.IsFalse(ExifOrientation.SwapsDimensions(SKEncodedOrigin.TopRight));
        Assert.IsFalse(ExifOrientation.SwapsDimensions(SKEncodedOrigin.BottomRight));
        Assert.IsFalse(ExifOrientation.SwapsDimensions(SKEncodedOrigin.BottomLeft));
        Assert.IsTrue(ExifOrientation.SwapsDimensions(SKEncodedOrigin.LeftTop));
        Assert.IsTrue(ExifOrientation.SwapsDimensions(SKEncodedOrigin.RightTop));
        Assert.IsTrue(ExifOrientation.SwapsDimensions(SKEncodedOrigin.RightBottom));
        Assert.IsTrue(ExifOrientation.SwapsDimensions(SKEncodedOrigin.LeftBottom));
    }

    private static SKBitmap CreateMarker()
    {
        var bitmap = new SKBitmap(new SKImageInfo(8, 6, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        bitmap.SetPixel(1, 1, SKColors.Red);
        return bitmap;
    }

    private static bool IsRed(SKColor color) =>
        color.Red > 200 && color.Green < 60 && color.Blue < 60;
}
