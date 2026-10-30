using System.Security.Cryptography;

namespace Memo.Core.Tests;

[TestClass]
public sealed class LinkDownloadTests : TestBase
{
    [TestMethod]
    public async Task Download_LocalServer_StoresContentAddressedFile()
    {
        var bytes = MediaFactory.Jpeg(400, 300, new DateOnly(2021, 4, 5));
        await using var server = new LocalHttpServer();
        server.MapFile("/photo.jpg", bytes);
        await using var library = await CreateLibraryAsync();
        using var service = new LinkDownloadService(library.Catalog, Policy(server));
        var progress = new CollectingProgress();

        var result = await service.DownloadAsync(Entry(server, "/photo.jpg", bytes), progress);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(LinkDownloadStatus.Completed, result.Status);
        Assert.AreEqual(Sha256(bytes), result.Sha256);
        Assert.IsNotNull(result.RelativePath);
        StringAssert.StartsWith(
            result.RelativePath!,
            $"media/{result.Sha256![..2]}/{result.Sha256}.");
        var fullPath = Path.Combine(
            library.Info.RootPath,
            result.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.IsTrue(File.Exists(fullPath));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fullPath));
        Assert.AreEqual(1, server.RequestCount("/photo.jpg"));

        Assert.IsTrue(progress.Values.Count > 0);
        Assert.AreEqual(bytes.Length, progress.Values[^1].BytesReceived);
        Assert.AreEqual(1d, progress.Values[^1].Fraction);
    }

    [TestMethod]
    public async Task Download_Resumes_AfterInterruptedTransfer()
    {
        var bytes = MediaFactory.Jpeg(640, 480);
        await using var server = new LocalHttpServer();
        server.MapFlaky("/flaky.jpg", bytes, bytes.Length / 2);
        await using var library = await CreateLibraryAsync();
        using var service = new LinkDownloadService(library.Catalog, Policy(server));
        var entry = Entry(server, "/flaky.jpg", bytes);

        var first = await service.DownloadAsync(entry);
        Assert.AreEqual(LinkDownloadStatus.Failed, first.Status);
        Assert.IsTrue(
            first.Diagnostics.Any(diagnostic => diagnostic.Code == "link.network-error"));

        var second = await service.DownloadAsync(entry);

        Assert.IsTrue(second.Success);
        Assert.AreEqual(2, server.RequestCount("/flaky.jpg"));
        var ranges = server.RangeHeaders("/flaky.jpg");
        Assert.IsNull(ranges[0]);
        Assert.IsNotNull(ranges[1]);
        StringAssert.StartsWith(ranges[1]!, "bytes=");
        var fullPath = Path.Combine(
            library.Info.RootPath,
            second.RelativePath!.Replace('/', Path.DirectorySeparatorChar));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fullPath));
    }

    [TestMethod]
    public async Task Download_MissingLink_ReportsExpired_WithoutToken()
    {
        const string token = "SECRET_SIGNATURE_987";
        await using var server = new LocalHttpServer();
        server.MapStatus("/gone.jpg", 404);
        await using var library = await CreateLibraryAsync();
        using var service = new LinkDownloadService(library.Catalog, Policy(server));
        var entry = new LinkIndexEntry(
            "gone",
            new DateTimeOffset(2022, 1, 1, 12, 0, 0, TimeSpan.Zero),
            LinkMediaType.Photo,
            server.Url($"/gone.jpg?token={token}"));

        var result = await service.DownloadAsync(entry);

        Assert.AreEqual(LinkDownloadStatus.Failed, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.expired"));
        var text = string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
        Assert.IsFalse(text.Contains(token, StringComparison.Ordinal));
        Assert.IsFalse(result.ToString().Contains(token, StringComparison.Ordinal));
        Assert.AreEqual(1, server.RequestCount("/gone.jpg"));
    }

    [TestMethod]
    public async Task Download_ContentLengthOverLimit_IsRejectedBeforeReading()
    {
        await using var server = new LocalHttpServer();
        server.MapFile("/large.bin", new byte[4096]);
        await using var library = await CreateLibraryAsync();
        using var service = new LinkDownloadService(
            library.Catalog,
            Policy(server, maxBytes: 1024));
        var entry = new LinkIndexEntry(
            "large",
            null,
            LinkMediaType.Photo,
            server.Url("/large.bin"));

        var result = await service.DownloadAsync(entry);

        Assert.AreEqual(LinkDownloadStatus.Failed, result.Status);
        Assert.IsTrue(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.size-exceeded"));
        Assert.AreEqual(1, server.RequestCount("/large.bin"));
    }

    [TestMethod]
    public async Task Download_DeclaredSizeOverLimit_IsRejectedWithoutRequest()
    {
        await using var server = new LocalHttpServer();
        server.MapFile("/photo.jpg", MediaFactory.Jpeg());
        await using var library = await CreateLibraryAsync();
        using var service = new LinkDownloadService(
            library.Catalog,
            Policy(server, maxBytes: 1024));
        var entry = new LinkIndexEntry(
            "declared-large",
            null,
            LinkMediaType.Photo,
            server.Url("/photo.jpg"),
            ExpectedSize: 2048);

        var result = await service.DownloadAsync(entry);

        Assert.AreEqual(LinkDownloadStatus.Failed, result.Status);
        Assert.IsTrue(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.size-exceeded"));
        Assert.AreEqual(0, server.RequestCount("/photo.jpg"));
    }

    [TestMethod]
    public async Task Download_RedirectOutsideAllowedHosts_IsRefused()
    {
        await using var server = new LocalHttpServer();
        server.MapStatus("/redirect", 302, "https://evil.example/secret.jpg");
        await using var library = await CreateLibraryAsync();
        using var service = new LinkDownloadService(library.Catalog, Policy(server));
        var entry = new LinkIndexEntry(
            "redirect",
            null,
            LinkMediaType.Photo,
            server.Url("/redirect"));

        var result = await service.DownloadAsync(entry);

        Assert.AreEqual(LinkDownloadStatus.Failed, result.Status);
        var diagnostic = result.Diagnostics.Single(
            item => item.Code == "link.redirect-refused");
        Assert.AreEqual("evil.example", diagnostic.Host);
        Assert.AreEqual(1, server.RequestCount("/redirect"));
    }

    [TestMethod]
    public async Task Download_RedirectToAnotherPort_IsRefused()
    {
        await using var target = new LocalHttpServer();
        target.MapFile("/photo.jpg", MediaFactory.Jpeg());
        await using var server = new LocalHttpServer();
        server.MapStatus("/redirect-port", 302, target.Url("/photo.jpg").AbsoluteUri);
        await using var library = await CreateLibraryAsync();
        using var service = new LinkDownloadService(library.Catalog, Policy(server));
        var entry = new LinkIndexEntry(
            "redirect-port",
            null,
            LinkMediaType.Photo,
            server.Url("/redirect-port"));

        var result = await service.DownloadAsync(entry);

        Assert.AreEqual(LinkDownloadStatus.Failed, result.Status);
        Assert.IsTrue(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.redirect-refused"));
        Assert.AreEqual(0, target.RequestCount("/photo.jpg"));
    }

    [TestMethod]
    public async Task Download_SameHostRedirect_IsFollowed()
    {
        var bytes = MediaFactory.Jpeg(320, 240);
        await using var server = new LocalHttpServer();
        server.MapFile("/target.jpg", bytes);
        server.MapStatus("/redirect-ok", 302, server.Url("/target.jpg").AbsoluteUri);
        await using var library = await CreateLibraryAsync();
        using var service = new LinkDownloadService(library.Catalog, Policy(server));
        var entry = Entry(server, "/redirect-ok", bytes);

        var result = await service.DownloadAsync(entry);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, server.RequestCount("/redirect-ok"));
        Assert.AreEqual(1, server.RequestCount("/target.jpg"));
    }

    [TestMethod]
    public async Task Download_WrongSha256_IsRejected_AndPartIsRemoved()
    {
        var bytes = MediaFactory.Jpeg(320, 240);
        await using var server = new LocalHttpServer();
        server.MapFile("/photo.jpg", bytes);
        await using var library = await CreateLibraryAsync();
        using var service = new LinkDownloadService(library.Catalog, Policy(server));
        var entry = new LinkIndexEntry(
            "wrong-hash",
            null,
            LinkMediaType.Photo,
            server.Url("/photo.jpg"),
            bytes.Length,
            new string('0', 64));

        var result = await service.DownloadAsync(entry);

        Assert.AreEqual(LinkDownloadStatus.Failed, result.Status);
        Assert.IsTrue(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.hash-mismatch"));
        Assert.AreEqual(0, Directory.GetFiles(
            Path.Combine(library.Info.RootPath, "imports"),
            "*.part").Length);
    }

    [TestMethod]
    public async Task Download_UnknownScheme_IsRejectedWithoutRequest()
    {
        await using var server = new LocalHttpServer();
        await using var library = await CreateLibraryAsync();
        using var service = new LinkDownloadService(library.Catalog, Policy(server));
        var entry = new LinkIndexEntry(
            "ftp",
            null,
            LinkMediaType.Photo,
            new Uri("ftp://127.0.0.1/photo.jpg"));

        var result = await service.DownloadAsync(entry);

        Assert.AreEqual(LinkDownloadStatus.Failed, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.url-scheme"));
        Assert.AreEqual(0, server.RequestCount("/photo.jpg"));
    }

    private static LinkDownloadPolicy Policy(LocalHttpServer server, long maxBytes = 8L * 1024 * 1024) =>
        new(
            new LinkUrlPolicy(new[] { "127.0.0.1" }, allowInsecureLoopback: true),
            maxBytes,
            TimeSpan.FromSeconds(10));

    private static LinkIndexEntry Entry(LocalHttpServer server, string path, byte[] bytes) =>
        new(
            "synthetic-1",
            new DateTimeOffset(2021, 7, 14, 18, 30, 0, TimeSpan.Zero),
            LinkMediaType.Photo,
            server.Url(path),
            bytes.Length,
            Sha256(bytes));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class CollectingProgress : IProgress<LinkDownloadProgress>
    {
        public List<LinkDownloadProgress> Values { get; } = new();

        public void Report(LinkDownloadProgress value)
        {
            lock (Values)
            {
                Values.Add(value);
            }
        }
    }
}
