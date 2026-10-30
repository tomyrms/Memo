using SkiaSharp;

namespace Memo.Media.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VideoFrameGrabberTests
{
    [TestMethod]
    public void GrabFrame_H264_ReturnsDisplaySizedFrame()
    {
        string videoPath = VideoFixtures.H264Path;

        using var grabber = new VideoFrameGrabber();
        using SKBitmap? frame = grabber.GrabFrame(videoPath, TimeSpan.FromSeconds(0.4));

        Assert.IsNotNull(frame, grabber.LastError);
        Assert.IsNull(grabber.LastError);
        Assert.AreEqual(320, frame.Width);
        Assert.AreEqual(240, frame.Height);
        SKColor center = frame.GetPixel(160, 120);
        Assert.IsTrue(
            center.Red > 180 && center.Green < 90 && center.Blue < 90,
            $"Rouge attendu, obtenu {center}.");
    }

    [TestMethod]
    public void GrabFrame_HevcWithoutWindowsExtension_ReturnsFrame()
    {
        string videoPath = VideoFixtures.HevcPath;

        using var grabber = new VideoFrameGrabber();
        using SKBitmap? frame = grabber.GrabFrame(videoPath, TimeSpan.FromSeconds(0.4));

        Assert.IsNotNull(frame, grabber.LastError);
        Assert.AreEqual(320, frame.Width);
        Assert.AreEqual(240, frame.Height);
        SKColor center = frame.GetPixel(160, 120);
        Assert.IsTrue(
            center.Blue > 150 && center.Red < 100 && center.Green < 100,
            $"Bleu attendu, obtenu {center}.");
    }

    [TestMethod]
    public void GrabFrame_IsReusableAndResetsLastError()
    {
        using var grabber = new VideoFrameGrabber();
        using var directory = new TempDirectory();

        SKBitmap? missing = grabber.GrabFrame(
            directory.File("absent.mp4"),
            TimeSpan.FromSeconds(0.4),
            TimeSpan.FromSeconds(1));
        Assert.IsNull(missing);
        Assert.IsFalse(string.IsNullOrWhiteSpace(grabber.LastError));

        using SKBitmap? first = grabber.GrabFrame(VideoFixtures.H264Path, TimeSpan.FromSeconds(0.2));
        Assert.IsNotNull(first, grabber.LastError);
        Assert.IsNull(grabber.LastError);

        using SKBitmap? second = grabber.GrabFrame(VideoFixtures.H264Path, TimeSpan.FromSeconds(0.5));
        Assert.IsNotNull(second, grabber.LastError);
        Assert.AreEqual(320, second.Width);
    }

    [TestMethod]
    public void GrabFrame_InvalidVideo_ReturnsNullWithoutCrash()
    {
        using var directory = new TempDirectory();
        string path = directory.File("pas-une-video.mp4");
        File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());

        using var grabber = new VideoFrameGrabber();
        using SKBitmap? frame = grabber.GrabFrame(
            path,
            TimeSpan.FromSeconds(0.4),
            TimeSpan.FromSeconds(2));

        Assert.IsNull(frame);
        Assert.IsFalse(string.IsNullOrWhiteSpace(grabber.LastError));
    }

    [TestMethod]
    public void GrabFrame_AcceptsVideosWithoutAudioTrack()
    {
        // Les vidéos de test sont générées avec -an : aucun flux audio n'existe.
        using var grabber = new VideoFrameGrabber();
        using SKBitmap? frame = grabber.GrabFrame(VideoFixtures.H264Path, TimeSpan.FromSeconds(0.5));

        Assert.IsNotNull(frame, grabber.LastError);
    }

    [TestMethod]
    public void GrabFrame_NegativePositionOrTimeout_ReturnsDiagnostic()
    {
        using var grabber = new VideoFrameGrabber();
        using var directory = new TempDirectory();

        Assert.IsNull(grabber.GrabFrame(VideoFixtures.H264Path, TimeSpan.FromSeconds(-1)));
        StringAssert.Contains(grabber.LastError, "négative");

        Assert.IsNull(grabber.GrabFrame(
            VideoFixtures.H264Path,
            TimeSpan.FromSeconds(0.4),
            TimeSpan.Zero));
        StringAssert.Contains(grabber.LastError, "strictement positif");
    }

    [TestMethod]
    public void Dispose_ThenGrab_ThrowsObjectDisposed()
    {
        var grabber = new VideoFrameGrabber();
        grabber.Dispose();
        grabber.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(
            () => grabber.GrabFrame("fichier-absent.mp4", TimeSpan.FromSeconds(0.4)));
    }
}
