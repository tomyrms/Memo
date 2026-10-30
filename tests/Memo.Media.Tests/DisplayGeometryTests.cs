using Memo.Core;
using SkiaSharp;

namespace Memo.Media.Tests;

[TestClass]
public sealed class DisplayGeometryTests
{
    [TestMethod]
    public void GetDisplaySize_RotatesQuarterTurns()
    {
        Assert.AreEqual(new DisplayDimensions(1920, 1080), DisplayGeometry.GetDisplaySize(1920, 1080, 0));
        Assert.AreEqual(new DisplayDimensions(1080, 1920), DisplayGeometry.GetDisplaySize(1920, 1080, 90));
        Assert.AreEqual(new DisplayDimensions(1920, 1080), DisplayGeometry.GetDisplaySize(1920, 1080, 180));
        Assert.AreEqual(new DisplayDimensions(1080, 1920), DisplayGeometry.GetDisplaySize(1920, 1080, 270));
        Assert.AreEqual(new DisplayDimensions(1080, 1920), DisplayGeometry.GetDisplaySize(1920, 1080, -90));
        Assert.AreEqual(new DisplayDimensions(1920, 1080), DisplayGeometry.GetDisplaySize(1920, 1080, 720));
    }

    [TestMethod]
    public void GetDisplaySize_InvalidRotationOrDimensions_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DisplayGeometry.GetDisplaySize(100, 50, 45));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DisplayGeometry.GetDisplaySize(0, 50, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DisplayGeometry.GetDisplaySize(100, -1, 0));
    }

    [TestMethod]
    public void GetDisplaySize_MediaInfo_UsesEncodedDimensionsAndRotation()
    {
        var media = new MediaInfo(
            "jpeg",
            1920,
            1080,
            90,
            null,
            false,
            null,
            null,
            null);

        Assert.AreEqual(new DisplayDimensions(1080, 1920), DisplayGeometry.GetDisplaySize(media));
        Assert.AreEqual(
            new DisplayDimensions(1920, 1080),
            DisplayGeometry.GetDisplaySize(media, dimensionsAreDecoderOutput: true));
    }

    [TestMethod]
    public void GetDisplaySize_MediaInfoWithoutDimensions_IsEmpty()
    {
        var media = new MediaInfo("jpeg", null, null, 0, null, false, null, null, null);

        DisplayDimensions dimensions = DisplayGeometry.GetDisplaySize(media);

        Assert.IsTrue(dimensions.IsEmpty);
        Assert.AreEqual(0, dimensions.Width);
        Assert.AreEqual(0, dimensions.Height);
    }

    [TestMethod]
    public void GetUniformRect_PortraitInLandscape_LetterboxesHorizontally()
    {
        var content = new DisplayDimensions(1080, 1920);
        var target = new SKSize(1920, 1080);

        SKRect rect = DisplayGeometry.GetUniformRect(content, target);

        Assert.AreEqual(607.5f, rect.Width, 0.01f);
        Assert.AreEqual(1080f, rect.Height, 0.01f);
        Assert.AreEqual(656.25f, rect.Left, 0.01f);
        Assert.AreEqual(0f, rect.Top, 0.01f);
        Assert.AreEqual(656.25f, 1920f - rect.Right, 0.01f);
    }

    [TestMethod]
    public void GetUniformRect_LandscapeInPortrait_LetterboxesVertically()
    {
        var content = new DisplayDimensions(1920, 1080);
        var target = new SKSize(1080, 1920);

        SKRect rect = DisplayGeometry.GetUniformRect(content, target);

        Assert.AreEqual(1080f, rect.Width, 0.01f);
        Assert.AreEqual(607.5f, rect.Height, 0.01f);
        Assert.AreEqual(0f, rect.Left, 0.01f);
        Assert.AreEqual(656.25f, rect.Top, 0.01f);
    }

    [TestMethod]
    public void GetUniformRect_MatchingAspect_FillsTarget()
    {
        SKRect rect = DisplayGeometry.GetUniformRect(
            new DisplayDimensions(800, 600),
            new SKSize(1600, 1200));

        Assert.AreEqual(0f, rect.Left, 0.01f);
        Assert.AreEqual(0f, rect.Top, 0.01f);
        Assert.AreEqual(1600f, rect.Width, 0.01f);
        Assert.AreEqual(1200f, rect.Height, 0.01f);
    }

    [TestMethod]
    public void GetUniformRect_MediaInfoWithRotation_UsesDisplayedAspect()
    {
        var media = new MediaInfo("jpeg", 1920, 1080, 90, null, false, null, null, null);

        SKRect rect = DisplayGeometry.GetUniformRect(media, new SKSize(1080, 1920));

        Assert.AreEqual(1080f, rect.Width, 0.01f);
        Assert.AreEqual(1920f, rect.Height, 0.01f);
    }

    [TestMethod]
    public void GetUniformRect_EmptyInputs_ReturnsEmptyRect()
    {
        SKRect emptyContent = DisplayGeometry.GetUniformRect(
            new DisplayDimensions(0, 0),
            new SKSize(100, 100));
        Assert.AreEqual(0f, emptyContent.Width);
        Assert.AreEqual(0f, emptyContent.Height);

        SKRect emptyTarget = DisplayGeometry.GetUniformRect(
            new DisplayDimensions(100, 100),
            new SKSize(0, 0));
        Assert.AreEqual(0f, emptyTarget.Width);
        Assert.AreEqual(0f, emptyTarget.Height);
    }
}
