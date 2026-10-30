using System.Text.Json;

namespace Memo.Core.Tests;

[TestClass]
public sealed class LibraryTests : TestBase
{
    [TestMethod]
    public async Task Create_CreatesExpectedLayout_AndOpenRestoresIdentity()
    {
        var root = NewDirectory();
        Guid id;
        await using (var library = await MemoLibrary.CreateAsync(root, "Ma bibliothèque", "Europe/Paris"))
        {
            id = library.Info.Id;
            Assert.AreEqual("Ma bibliothèque", library.Info.Name);
            Assert.AreEqual("Europe/Paris", library.Info.TimeZoneId);
            Assert.IsTrue(File.Exists(Path.Combine(root, "catalog.db")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "library.json")));
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "media")));
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "derived", "thumbs")));
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "imports")));
            Assert.AreEqual("Europe/Paris", library.Settings.Current.TimeZoneId);
            await library.Catalog.VacuumAsync();
        }

        await using var reopened = await MemoLibrary.OpenAsync(root);
        Assert.AreEqual(id, reopened.Info.Id);
        Assert.AreEqual("Ma bibliothèque", reopened.Info.Name);
        Assert.AreEqual("Europe/Paris", reopened.Info.TimeZoneId);
        Assert.AreEqual(Path.GetFullPath(root), reopened.Info.RootPath);
    }

    [TestMethod]
    public async Task Create_RefusesToOverwriteExistingLibrary()
    {
        var root = NewDirectory();
        await using (var library = await MemoLibrary.CreateAsync(root, "Première", "UTC"))
        {
            Assert.IsNotNull(library.Info);
        }

        await ThrowsAsync<InvalidOperationException>(() =>
            MemoLibrary.CreateAsync(root, "Seconde", "UTC"));
    }

    [TestMethod]
    public async Task Open_RejectsUnknownDirectory_WithClearMessage()
    {
        var root = NewDirectory();
        Directory.CreateDirectory(root);
        var exception = await ThrowsAsync<InvalidOperationException>(() => MemoLibrary.OpenAsync(root));
        StringAssert.Contains(exception.Message, "library.json");
    }

    [TestMethod]
    public async Task Open_RejectsUnknownManifestFormat()
    {
        var root = NewDirectory();
        Directory.CreateDirectory(root);
        var document = new
        {
            format = "another-application",
            version = 1,
            id = Guid.NewGuid(),
            name = "Inconnue",
            timeZoneId = "UTC",
            createdAt = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "library.json"),
            JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var exception = await ThrowsAsync<InvalidOperationException>(() => MemoLibrary.OpenAsync(root));
        StringAssert.Contains(exception.Message, "inconnue");
    }

    [TestMethod]
    public async Task Open_RejectsMissingCatalog()
    {
        var root = NewDirectory();
        await using (var library = await MemoLibrary.CreateAsync(root, "Sans catalogue", "UTC"))
        {
            Assert.IsNotNull(library.Info);
        }

        File.Delete(Path.Combine(root, "catalog.db"));
        var exception = await ThrowsAsync<InvalidOperationException>(() => MemoLibrary.OpenAsync(root));
        StringAssert.Contains(exception.Message, "catalog.db");
    }
}
