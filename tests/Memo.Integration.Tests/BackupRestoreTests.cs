using Memo.Core;
using Memo.TestSupport;

namespace Memo.Integration.Tests;

[TestClass]
public sealed class BackupRestoreTests : IntegrationTestBase
{
    [TestMethod]
    public async Task BackupThenRestore_IntoEmptyFolder_ReproducesLibraryIntegrally()
    {
        var video = SyntheticMedia
            .Video(50, new DateTimeOffset(2023, 5, 6, 10, 0, 0, TimeSpan.Zero), rotation: 90)
            .WithOverlay(caption: "CALQUE VIDEO");
        var favorite = SyntheticMedia.Photo(51, "PHOTO FAVORITE", seed: 51);
        var simple = SyntheticMedia.Photo(52, "PHOTO SIMPLE", seed: 52);

        var sourceZip = ArchiveBuilder.WriteComplete(
            Path.Combine(Root, "source-sauvegarde.zip"),
            new[] { video, favorite, simple });

        var sourceRoot = Path.Combine(Root, "bibliotheque-source");
        var backupPath = Path.Combine(Root, "sauvegarde.zip");
        var restoredRoot = Path.Combine(Root, "bibliotheque-restauree");
        Guid libraryId;
        long videoId;
        long favoriteId;
        long simpleId;
        long albumId;

        string sourceSnapshot;
        await using (var library = await MemoLibrary.CreateAsync(
                         sourceRoot,
                         "Bibliothèque sauvegardée",
                         "Europe/Zurich"))
        {
            libraryId = library.Info.Id;
            var space = await CreateSpaceAsync(library, "Compte sauvegardé");

            var import = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { sourceZip }));
            Assert.AreEqual(0, import.Counters.Errors);
            Assert.AreEqual(3, import.Counters.MemoriesCreated);

            var cards = await library.Catalog.QueryAsync(new MemoryQuery());
            var byKey = cards.ToDictionary(card => card.ExternalKey, StringComparer.Ordinal);
            videoId = byKey[video.Id.ToString("D")].Id;
            favoriteId = byKey[favorite.Id.ToString("D")].Id;
            simpleId = byKey[simple.Id.ToString("D")].Id;

            await library.Catalog.SetDateOverrideAsync(
                videoId,
                new DateOnly(2019, 6, 7),
                DatePrecision.Day,
                "date choisie");
            await library.Catalog.SetFavoriteAsync(new[] { favoriteId }, true);
            await library.Catalog.SetExcludedAsync(new[] { favoriteId }, true);
            await library.Catalog.SetLocationAsync(favoriteId, 45.7640, 4.8357, "Lyon");
            await ((MemoCatalog)library.Catalog).SetNoteAsync(favoriteId, "note de sauvegarde");

            var album = await library.Catalog.CreateCollectionAsync(
                "Album sauvegardé",
                CollectionKind.Album);
            albumId = album.Id;
            await library.Catalog.AddToCollectionAsync(album.Id, new[] { favoriteId, simpleId });
            await library.Catalog.ReorderCollectionAsync(album.Id, new[] { simpleId, favoriteId });

            await library.Settings.SaveAsync(
                library.Settings.Current with
                {
                    PhotoDurationSeconds = 7,
                    Theme = "light",
                    RemindersEnabled = true,
                    ReminderTime = "07:30",
                });

            var backup = await library.Backup.CreateBackupAsync(backupPath);
            Assert.AreEqual(libraryId, backup.Manifest.LibraryId);
            Assert.AreEqual(3, backup.Manifest.MemoryCount);
            Assert.AreEqual(4, backup.Manifest.SourceCount);
            Assert.AreEqual(1, backup.Manifest.Version);
            CollectionAssert.Contains(
                backup.Manifest.Included.ToArray(),
                "media/");
            CollectionAssert.Contains(
                backup.Manifest.Excluded.ToArray(),
                "derived/");
            Assert.IsTrue(File.Exists(backup.Path));

            sourceSnapshot = await LibrarySnapshot.FullAsync(library);

            Assert.IsFalse(Directory.Exists(restoredRoot));
            await library.Backup.RestoreAsync(backupPath, restoredRoot);
            Assert.IsTrue(Directory.Exists(restoredRoot));
            Assert.IsTrue(File.Exists(Path.Combine(restoredRoot, "catalog.db")));
            Assert.IsTrue(File.Exists(Path.Combine(restoredRoot, "library.json")));
        }

        await using var restored = await MemoLibrary.OpenAsync(restoredRoot);
        Assert.AreEqual(libraryId, restored.Info.Id);
        Assert.AreEqual("Bibliothèque sauvegardée", restored.Info.Name);
        Assert.AreEqual("Europe/Zurich", restored.Info.TimeZoneId);

        var restoredSnapshot = await LibrarySnapshot.FullAsync(restored);
        Assert.AreEqual(
            sourceSnapshot,
            restoredSnapshot,
            "La bibliothèque restaurée diffère de la source au-delà du nombre de fichiers.");

        var restoredCards = await restored.Catalog.QueryAsync(new MemoryQuery());
        Assert.AreEqual(3, restoredCards.Count);
        var restoredByKey = restoredCards.ToDictionary(card => card.ExternalKey, StringComparer.Ordinal);

        var restoredVideo = restoredByKey[video.Id.ToString("D")];
        Assert.AreEqual(videoId, restoredVideo.Id);
        Assert.AreEqual(MemoryStatus.Complete, restoredVideo.Status);
        Assert.AreEqual(new DateOnly(2019, 6, 7), restoredVideo.LocalDate);
        Assert.AreEqual(DateSource.User, restoredVideo.DateSource);
        Assert.AreEqual(1, restoredVideo.OverlayCount);

        var restoredFavorite = restoredByKey[favorite.Id.ToString("D")];
        Assert.AreEqual(favoriteId, restoredFavorite.Id);
        Assert.IsTrue(restoredFavorite.IsFavorite);
        Assert.IsTrue(restoredFavorite.IsExcluded);
        Assert.AreEqual(45.7640, restoredFavorite.Latitude);
        Assert.AreEqual("Lyon", restoredFavorite.LocationLabel);

        var restoredDetails = await restored.Catalog.GetDetailsAsync(favoriteId);
        Assert.IsNotNull(restoredDetails);
        Assert.AreEqual("note de sauvegarde", restoredDetails.Notes);
        Assert.AreEqual(1, restoredDetails.Collections.Count);
        Assert.AreEqual(albumId, restoredDetails.Collections[0].Id);

        var albumItems = await restored.Catalog.GetCollectionItemsAsync(albumId);
        CollectionAssert.AreEqual(
            new[] { simpleId, favoriteId },
            albumItems.Select(card => card.Id).ToArray());

        var collections = await restored.Catalog.GetCollectionsAsync();
        Assert.AreEqual(1, collections.Count);
        Assert.AreEqual("Album sauvegardé", collections[0].Name);
        Assert.AreEqual(2, collections[0].ItemCount);

        Assert.AreEqual(7, restored.Settings.Current.PhotoDurationSeconds);
        Assert.AreEqual("light", restored.Settings.Current.Theme);
        Assert.IsTrue(restored.Settings.Current.RemindersEnabled);
        Assert.AreEqual("07:30", restored.Settings.Current.ReminderTime);

        foreach (var card in restoredCards)
        {
            Assert.IsNotNull(card.MainAssetId);
            Assert.IsTrue(File.Exists(restored.Catalog.GetAssetPath(card.MainAssetId.Value)));
            if (card.OverlayAssetId is { } overlayId)
            {
                Assert.IsTrue(File.Exists(restored.Catalog.GetAssetPath(overlayId)));
            }
        }

        var restoredFiles = Directory
            .EnumerateFiles(restoredRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(restoredRoot, path).Replace('\\', '/'))
            .Where(relative => relative.StartsWith("media/", StringComparison.Ordinal))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToArray();
        Assert.AreEqual(4, restoredFiles.Length);
    }
}
