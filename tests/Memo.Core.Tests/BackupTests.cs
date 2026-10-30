using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Memo.Core.Tests;

[TestClass]
public sealed class BackupTests : TestBase
{
    [TestMethod]
    public async Task BackupAndRestore_PreserveRelationsAndSettings()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "backup-source.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(300, 200, new DateOnly(2020, 5, 1))),
            ($"memories/{uuid}-overlay.png", MediaFactory.Png(300, 200)));

        var sourceRoot = NewDirectory("source");
        Guid libraryId;
        long memoryId;
        await using (var library = await MemoLibrary.CreateAsync(sourceRoot, "À sauvegarder", "Europe/Paris"))
        {
            libraryId = library.Info.Id;
            var space = await NewSpaceAsync(library);
            await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
            var card = (await library.Catalog.QueryAsync(new MemoryQuery()))[0];
            memoryId = card.Id;
            await library.Catalog.SetFavoriteAsync(new[] { memoryId }, true);
            await library.Catalog.SetDateOverrideAsync(
                memoryId, new DateOnly(2019, 6, 7), DatePrecision.Day, "date choisie");
            await library.Catalog.SetLocationAsync(memoryId, 45.7640, 4.8357, "Lyon");
            await ((MemoCatalog)library.Catalog).SetNoteAsync(memoryId, "note de sauvegarde");
            var album = await library.Catalog.CreateCollectionAsync("Album sauvegardé", CollectionKind.Album);
            await library.Catalog.AddToCollectionAsync(album.Id, new[] { memoryId });
            await library.Settings.SaveAsync(
                library.Settings.Current with { PhotoDurationSeconds = 7, Theme = "light" });
            PathFactory.WriteFile(library.Info.RootPath, "derived/thumbs/cache.bin", new byte[] { 1, 2, 3 });

            var backup = await library.Backup.CreateBackupAsync(Path.Combine(Root, "backup.zip"));
            Assert.IsTrue(File.Exists(backup.Path));
            Assert.AreEqual(libraryId, backup.Manifest.LibraryId);
            Assert.AreEqual(1, backup.Manifest.MemoryCount);

            using (var archive = ZipFile.OpenRead(backup.Path))
            {
                var names = archive.Entries.Select(entry => entry.FullName).ToList();
                Assert.IsTrue(names.Contains("catalog.db"));
                Assert.IsTrue(names.Contains("library.json"));
                Assert.IsTrue(names.Contains("manifest.json"));
                Assert.IsTrue(names.Any(name => name.StartsWith("media/", StringComparison.Ordinal)));
                Assert.IsFalse(names.Any(name => name.StartsWith("derived/", StringComparison.Ordinal)));
            }
        }

        var restoredRoot = Path.Combine(Root, "restored");
        await using (var original = await MemoLibrary.OpenAsync(sourceRoot))
        {

            await original.Backup.RestoreAsync(Path.Combine(Root, "backup.zip"), restoredRoot);
        }

        await using var restored = await MemoLibrary.OpenAsync(restoredRoot);
        Assert.AreEqual(libraryId, restored.Info.Id);
        var restoredCard = (await restored.Catalog.QueryAsync(new MemoryQuery()))[0];
        Assert.AreEqual(memoryId, restoredCard.Id);
        Assert.IsTrue(restoredCard.IsFavorite);
        Assert.AreEqual(new DateOnly(2019, 6, 7), restoredCard.LocalDate);
        Assert.AreEqual(DateSource.User, restoredCard.DateSource);
        Assert.AreEqual(45.7640, restoredCard.Latitude);
        Assert.AreEqual("Lyon", restoredCard.LocationLabel);
        Assert.IsTrue(File.Exists(restored.Catalog.GetAssetPath(restoredCard.MainAssetId!.Value)));
        Assert.IsTrue(File.Exists(restored.Catalog.GetAssetPath(restoredCard.OverlayAssetId!.Value)));
        Assert.AreEqual(7, restored.Settings.Current.PhotoDurationSeconds);

        var details = await restored.Catalog.GetDetailsAsync(restoredCard.Id);
        Assert.IsNotNull(details);
        Assert.AreEqual("note de sauvegarde", details.Notes);
        Assert.AreEqual(1, details.Collections.Count);
        Assert.AreEqual("Album sauvegardé", details.Collections[0].Name);
    }

    [TestMethod]
    public async Task Restore_RefusesNonEmptyDestination_WithoutOverwriting()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "restore-source.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2020, 1, 1))));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var backupPath = Path.Combine(Root, "restore.zip");
        await library.Backup.CreateBackupAsync(backupPath);

        var destination = Path.Combine(Root, "occupied");
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "important.txt");
        await File.WriteAllTextAsync(sentinel, "à conserver");

        await ThrowsAsync<InvalidOperationException>(() =>
            library.Backup.RestoreAsync(backupPath, destination));
        Assert.AreEqual("à conserver", await File.ReadAllTextAsync(sentinel));
    }

    [TestMethod]
    public async Task ExportSelection_CopiesReadableFilesAndMetadata_WithoutChangingSources()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "export-source.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(200, 150, new DateOnly(2021, 9, 10))),
            ($"memories/{uuid}-overlay.png", MediaFactory.Png(200, 150)));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var card = (await library.Catalog.QueryAsync(new MemoryQuery()))[0];
        await library.Catalog.SetDateOverrideAsync(card.Id, new DateOnly(2021, 9, 11), DatePrecision.Day);

        var mainPath = library.Catalog.GetAssetPath(card.MainAssetId!.Value);
        var overlayPath = library.Catalog.GetAssetPath(card.OverlayAssetId!.Value);
        var mainHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(mainPath)));
        var overlayHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(overlayPath)));

        var destination = Path.Combine(Root, "exported");
        await library.Backup.ExportSelectionAsync(new[] { card.Id }, destination, includeOverlays: true);

        var files = Directory.GetFiles(destination).Select(Path.GetFileName).ToList();
        var mainName = files.Single(name => name!.EndsWith(".jpg", StringComparison.Ordinal));
        var overlayName = files.Single(name => name!.EndsWith("-overlay.png", StringComparison.Ordinal));
        Assert.IsTrue(
            mainName!.StartsWith($"20210911-000000-{uuid}.", StringComparison.Ordinal),
            $"Nom réel : {mainName} ; attendu : 20210911-000000-{uuid}.…");
        Assert.IsTrue(overlayName!.StartsWith($"20210911-000000-{uuid}-overlay", StringComparison.Ordinal));
        Assert.IsTrue(files.Contains("metadata.json"));

        using (var metadata = JsonDocument.Parse(
                   await File.ReadAllTextAsync(Path.Combine(destination, "metadata.json"))))
        {
            var item = metadata.RootElement.GetProperty("items")[0];
            Assert.AreEqual(card.ExternalKey, item.GetProperty("externalKey").GetString());
            Assert.AreEqual("2021-09-11", item.GetProperty("localDate").GetString());
            Assert.AreEqual(2, item.GetProperty("files").GetArrayLength());
            Assert.IsTrue(item.GetProperty("dateCandidates").GetArrayLength() >= 1);
            Assert.IsTrue(item.GetProperty("files")[0].GetProperty("provenance")
                .GetProperty("entryPath").GetString()!.StartsWith("memories/", StringComparison.Ordinal));
        }

        Assert.AreEqual(mainHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(mainPath))));
        Assert.AreEqual(overlayHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(overlayPath))));
    }

    [TestMethod]
    public async Task Restore_RejectsBackupWithoutManifest()
    {
        var invalid = Path.Combine(Root, "invalid-backup.zip");
        using (var archive = ZipFile.Open(invalid, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("library.json");
            await using var stream = entry.Open();
            await stream.WriteAsync("{}"u8.ToArray());
        }

        await using var library = await CreateLibraryAsync();
        await ThrowsAsync<InvalidDataException>(() =>
            library.Backup.RestoreAsync(invalid, Path.Combine(Root, "nowhere")));
    }
}
