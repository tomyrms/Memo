using System.Text;
using System.Text.Json;

namespace Memo.Core.Tests;

[TestClass]
public sealed class LinkIndexTests
{
    private static readonly LinkUrlPolicy Policy = new(new[] { "media.example.test" });

    [TestMethod]
    public void DefaultRegistry_HasNoAdapter_AndDoesNotActivateOnResemblance()
    {
        var json = Index(Entry("synthetic-1", "https://media.example.test/photo.jpg"));

        Assert.IsFalse(
            LinkIndexAdapterRegistry.Empty.Detect(Encoding.UTF8.GetBytes(json)).Detected);
        var result = LinkIndexAdapterRegistry.Empty.Read(Encoding.UTF8.GetBytes(json), Options());

        Assert.IsFalse(result.Recognized);
        Assert.AreEqual(0, result.Entries.Count);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic => diagnostic.Code == "index.no-adapter"));
    }

    [TestMethod]
    public void UnvalidatedAdapter_RequiresExplicitOptIn()
    {
        var threw = false;
        try
        {
            _ = new LinkIndexAdapterRegistry(new ILinkIndexAdapter[] { new MemoIndexV1Adapter() });
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Un adaptateur non validé doit être refusé par défaut.");

        var registry = new LinkIndexAdapterRegistry(
            new ILinkIndexAdapter[] { new MemoIndexV1Adapter() },
            allowUnvalidatedAdapters: true);
        Assert.AreEqual(1, registry.Adapters.Count);
    }

    [TestMethod]
    public void Detection_RequiresExactMarker_NotMereResemblance()
    {
        var adapter = new MemoIndexV1Adapter();
        var valid = Index(Entry("synthetic-1", "https://media.example.test/photo.jpg"));

        Assert.IsTrue(adapter.Detect(Encoding.UTF8.GetBytes(valid)).Detected);
        Assert.IsFalse(adapter.Detect(Encoding.UTF8.GetBytes("""{"entries":[]}""")).Detected);
        Assert.IsFalse(
            adapter.Detect(Encoding.UTF8.GetBytes(
                """{"format":"memo-index-v1","schemaVersion":2,"entries":[]}""")).Detected);
        Assert.IsFalse(
            adapter.Detect(Encoding.UTF8.GetBytes(
                """{"format":"snapchat-index","schemaVersion":1,"entries":[]}""")).Detected);
        Assert.IsFalse(adapter.Detect(Encoding.UTF8.GetBytes("pas du json")).Detected);
    }

    [TestMethod]
    public void ValidEntry_IsRead_WithoutPromotingMetadata()
    {
        var result = Read(Index(Entry(
            "synthetic-1",
            "https://media.example.test/photo.jpg",
            date: "2021-07-14T18:30:00Z",
            size: 1234)));

        Assert.IsTrue(result.Recognized);
        Assert.IsFalse(result.HasErrors);
        var entry = AssertSingle(result.Entries);
        Assert.AreEqual("synthetic-1", entry.Id);
        Assert.AreEqual(
            new DateTimeOffset(2021, 7, 14, 18, 30, 0, TimeSpan.Zero),
            entry.Date);
        Assert.AreEqual(LinkMediaType.Photo, entry.Type);
        Assert.AreEqual(1234, entry.ExpectedSize);
        Assert.IsNull(entry.ExpectedSha256);
        Assert.AreEqual("https://media.example.test/photo.jpg", entry.Url.AbsoluteUri);
    }

    [TestMethod]
    public void UnknownScheme_IsRejected()
    {
        var result = Read(Index(Entry("synthetic-1", "ftp://media.example.test/photo.jpg")));

        Assert.IsTrue(result.Recognized);
        Assert.AreEqual(0, result.Entries.Count);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.url-scheme"));
    }

    [TestMethod]
    public void HttpUrl_IsRejected()
    {
        var result = Read(Index(Entry("synthetic-1", "http://media.example.test/photo.jpg")));

        Assert.IsTrue(result.Recognized);
        Assert.AreEqual(0, result.Entries.Count);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.url-scheme"));
    }

    [TestMethod]
    public void OutOfDomainUrl_IsRejected()
    {
        var result = Read(Index(Entry("synthetic-1", "https://evil.example/photo.jpg")));

        Assert.IsTrue(result.Recognized);
        Assert.AreEqual(0, result.Entries.Count);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.url-host"));
    }

    [TestMethod]
    public void Subdomain_IsNotImplicitlyAllowed()
    {
        var result = Read(Index(Entry(
            "synthetic-1",
            "https://sub.media.example.test/photo.jpg")));

        Assert.IsTrue(result.Recognized);
        Assert.AreEqual(0, result.Entries.Count);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.url-host"));
    }

    [TestMethod]
    public void DiagnosticsAndEntryText_DoNotContainUrlToken()
    {
        const string token = "SUPER_SECRET_TOKEN_123";
        var result = Read(Index(
            Entry("ok", $"https://media.example.test/photo.jpg?token={token}"),
            Entry("rejected", $"http://media.example.test/photo2.jpg?token={token}")));

        Assert.AreEqual(1, result.Entries.Count);
        Assert.IsTrue(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == "link.url-scheme"));

        var diagnosticsText = string.Join(
            "\n",
            result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
        Assert.IsFalse(diagnosticsText.Contains(token, StringComparison.Ordinal));
        Assert.IsFalse(result.Entries[0].ToString().Contains(token, StringComparison.Ordinal));
        Assert.IsFalse(result.Entries[0].SafeUrl.Contains(token, StringComparison.Ordinal));
    }

    [TestMethod]
    public void DuplicateEntryIds_AreRejectedIndividually()
    {
        var result = Read(Index(
            Entry("same", "https://media.example.test/a.jpg"),
            Entry("same", "https://media.example.test/b.jpg")));

        Assert.AreEqual(1, result.Entries.Count);
        Assert.IsTrue(
            result.Diagnostics.Any(diagnostic => diagnostic.Code == "index.entry-duplicate"));
    }

    [TestMethod]
    public void SerializedEntry_DoesNotContainUrlOrToken()
    {
        const string token = "SERIALIZED_SECRET_456";
        var entry = new LinkIndexEntry(
            "synthetic-1",
            null,
            LinkMediaType.Photo,
            new Uri($"https://media.example.test/photo.jpg?token={token}"));

        var json = JsonSerializer.Serialize(entry);

        Assert.IsFalse(json.Contains(token, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("photo.jpg", StringComparison.Ordinal));
        StringAssert.Contains(json, "media.example.test");
    }

    [TestMethod]
    public async Task ReadAsync_RejectsOversizedIndex()
    {
        var registry = new LinkIndexAdapterRegistry(
            new ILinkIndexAdapter[] { new MemoIndexV1Adapter() },
            allowUnvalidatedAdapters: true);
        var json = Encoding.UTF8.GetBytes(
            Index(Entry("synthetic-1", "https://media.example.test/photo.jpg")));

        var result = await registry.ReadAsync(
            new MemoryStream(json),
            new LinkIndexReadOptions(Policy, MaxIndexBytes: 16));

        Assert.IsFalse(result.Recognized);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic => diagnostic.Code == "index.too-large"));
    }

    private static LinkIndexResult Read(string json)
    {
        var registry = new LinkIndexAdapterRegistry(
            new ILinkIndexAdapter[] { new MemoIndexV1Adapter() },
            allowUnvalidatedAdapters: true);
        return registry.Read(Encoding.UTF8.GetBytes(json), Options());
    }

    private static LinkIndexReadOptions Options() => new(Policy);

    private static string Index(params string[] entries) =>
        "{\"format\":\"memo-index-v1\",\"schemaVersion\":1,\"entries\":[" +
        string.Join(",", entries) +
        "]}";

    private static string Entry(
        string id,
        string url,
        string type = "photo",
        string? date = "2021-07-14T18:30:00Z",
        long? size = null,
        string? sha256 = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["type"] = type,
            ["url"] = url,
        };
        if (date is not null)
        {
            fields["date"] = date;
        }

        if (size is not null)
        {
            fields["size"] = size;
        }

        if (sha256 is not null)
        {
            fields["sha256"] = sha256;
        }

        return JsonSerializer.Serialize(fields);
    }

    private static T AssertSingle<T>(IReadOnlyList<T> items)
    {
        Assert.AreEqual(1, items.Count, "Une seule entrée était attendue.");
        return items[0];
    }
}
