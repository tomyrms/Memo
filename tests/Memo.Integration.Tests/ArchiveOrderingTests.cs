using Memo.Core;

namespace Memo.Integration.Tests;

[TestClass]
public sealed class ArchiveOrderingTests : IntegrationTestBase
{
    [TestMethod]
    public async Task ThreeBatches_InEveryPermutation_ProduceIdenticalLibrary()
    {
        // Le lot A porte les médias, le lot B les calques, le lot C l'index et les
        // métadonnées. Les horodatages ZIP sont volontairement tous différents.
        var video = SyntheticMedia
            .Video(1, new DateTimeOffset(2021, 7, 14, 22, 15, 0, TimeSpan.Zero), rotation: 90)
            .WithOverlay(caption: "CALQUE VIDEO");
        var photo = SyntheticMedia
            .Photo(2, "PHOTO SANS DATE", seed: 2)
            .WithOverlay(caption: "CALQUE PHOTO", seed: 2);

        var zips = new[]
        {
            ArchiveBuilder.WriteMains(
                Path.Combine(Root, "lot-a-medias.zip"),
                new[] { video, photo },
                new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            ArchiveBuilder.WriteOverlays(
                Path.Combine(Root, "lot-b-calques.zip"),
                new[] { video, photo },
                new DateTimeOffset(1985, 6, 15, 3, 4, 5, TimeSpan.Zero)),
            ArchiveBuilder.WriteMetadata(
                Path.Combine(Root, "lot-c-metadonnees.zip"),
                new[] { video, photo },
                new DateTimeOffset(1999, 12, 31, 23, 59, 0, TimeSpan.Zero)),
        };

        var permutations = new[]
        {
            new[] { 0, 1, 2 },
            new[] { 0, 2, 1 },
            new[] { 1, 0, 2 },
            new[] { 1, 2, 0 },
            new[] { 2, 0, 1 },
            new[] { 2, 1, 0 },
        };

        var videoKey = video.Id.ToString("D");
        var photoKey = photo.Id.ToString("D");
        string? reference = null;

        foreach (var permutation in permutations)
        {
            var label = string.Join("-", permutation.Select(index => "ABC"[index]));
            await using var library = await CreateLibraryAsync();
            var space = await CreateSpaceAsync(library);
            var paths = permutation.Select(index => zips[index]).ToArray();

            var result = await library.Import.RunAsync(new ImportRequest(space.Id, paths));
            Assert.IsFalse(result.Cancelled, label);
            Assert.AreEqual(0, result.Counters.Errors, label);
            Assert.AreEqual(3, result.Counters.ArchivesSeen, label);
            Assert.AreEqual(3, result.Counters.ArchivesImported, label);
            Assert.AreEqual(6, result.Counters.EntriesSeen, label);
            Assert.AreEqual(2, result.Counters.MediaImported, label);
            Assert.AreEqual(2, result.Counters.OverlaysImported, label);
            Assert.AreEqual(2, result.Counters.MemoriesCreated, label);
            Assert.AreEqual(2, result.Counters.MemoriesUpdated, label);
            Assert.AreEqual(0, result.Counters.DuplicatesSkipped, label);
            Assert.AreEqual(0, result.Counters.Unrecognized, label);

            var cards = await library.Catalog.QueryAsync(new MemoryQuery());
            Assert.AreEqual(2, cards.Count, label);
            var byKey = cards.ToDictionary(card => card.ExternalKey, StringComparer.Ordinal);
            var importedVideo = byKey[videoKey];
            var importedPhoto = byKey[photoKey];

            Assert.AreEqual(MemoryStatus.Complete, importedVideo.Status, label);
            Assert.AreEqual(MediaKind.Video, importedVideo.Kind, label);
            Assert.AreEqual(new DateOnly(2021, 7, 15), importedVideo.LocalDate, label);
            Assert.AreEqual(DateSource.Mp4CreationTime, importedVideo.DateSource, label);
            Assert.AreEqual(DatePrecision.Second, importedVideo.DatePrecision, label);
            Assert.AreEqual(1, importedVideo.OverlayCount, label);
            Assert.IsNotNull(importedVideo.MainAssetId, label);
            Assert.IsNotNull(importedVideo.OverlayAssetId, label);
            Assert.AreEqual(4000, importedVideo.DurationMs, label);
            Assert.AreEqual("mp4", importedVideo.Format, label);

            Assert.AreEqual(MemoryStatus.Complete, importedPhoto.Status, label);
            Assert.AreEqual(MediaKind.Photo, importedPhoto.Kind, label);
            Assert.IsNull(importedPhoto.LocalDate, label);
            Assert.AreEqual(DateSource.Unknown, importedPhoto.DateSource, label);
            Assert.AreEqual(1, importedPhoto.OverlayCount, label);
            Assert.IsNotNull(importedPhoto.MainAssetId, label);
            Assert.IsNotNull(importedPhoto.OverlayAssetId, label);
            Assert.AreEqual("jpeg", importedPhoto.Format, label);

            foreach (var card in cards)
            {
                var details = await library.Catalog.GetDetailsAsync(card.Id);
                Assert.IsNotNull(details, label);
                Assert.AreEqual(2, details.Sources.Count, label);
                Assert.AreEqual(1, details.Sources.Count(source => source.Role == MediaRole.Main), label);
                Assert.AreEqual(1, details.Sources.Count(source => source.Role == MediaRole.Overlay), label);
            }

            var stats = await library.Catalog.GetStorageStatsAsync();
            Assert.AreEqual(2, stats.Memories, label);
            Assert.AreEqual(4, stats.Assets, label);
            Assert.AreEqual(4, stats.Sources, label);

            var snapshot = await LibrarySnapshot.MemoriesAsync(library);
            reference ??= snapshot;
            Assert.AreEqual(
                reference,
                snapshot,
                $"L'état final dépend de l'ordre d'import {label}.");
        }

        Assert.IsNotNull(reference);
        StringAssert.Contains(reference, $"|{videoKey}|");
        StringAssert.Contains(reference, $"|{photoKey}|");
    }
}
