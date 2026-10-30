namespace Memo.Media.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MediaDiagnosticsTests
{
    [TestMethod]
    public void Check_ReportsSkiaWebPAndLibVlc()
    {
        MediaDiagnosticsReport report = MediaDiagnostics.Check();

        Assert.HasCount(2, report.Items);
        Assert.IsTrue(
            report.Items.Any(item => item.Key == "skia-webp" && item.Available),
            report.ToText());
        Assert.IsTrue(
            report.Items.Any(item => item.Key == "libvlc" && item.Available),
            report.ToText());
        Assert.IsTrue(report.IsHealthy, report.ToText());
        Assert.AreEqual(2, report.AvailableCount);

        string text = report.ToText();
        StringAssert.Contains(text, "Moteur média");
        StringAssert.Contains(text, "WebP");
        StringAssert.Contains(text, "libvlc");
    }

    [TestMethod]
    public void Report_WithMissingDependency_IsUnhealthyAndReadable()
    {
        var report = new MediaDiagnosticsReport(
        [
            new MediaDiagnosticItem("skia-webp", "Images", true, "WebP décodé."),
            new MediaDiagnosticItem("libvlc", "Vidéo", false, "Bibliothèque introuvable."),
        ]);

        Assert.IsFalse(report.IsHealthy);
        Assert.AreEqual(1, report.AvailableCount);
        StringAssert.Contains(report.Summary, "1 dépendance(s) indisponible(s)");
        StringAssert.Contains(report.ToText(), "Vidéo : indisponible — Bibliothèque introuvable.");
    }
}
