using System.Security.Cryptography;
using Memo.Core;

namespace Memo.Integration.Tests;

[TestClass]
public sealed class SpaceIsolationTests : IntegrationTestBase
{
    [TestMethod]
    public async Task SameUuid_InTwoSpaces_WithDifferentContent_StaysSeparate()
    {
        var photo = SyntheticMedia.Photo(20, "ESPACE A", seed: 20);
        var video = SyntheticMedia.Video(
            20,
            new DateTimeOffset(2022, 3, 4, 8, 0, 0, TimeSpan.Zero),
            rotation: 90,
            durationMs: 6500);

        var zipA = ArchiveBuilder.WriteComplete(Path.Combine(Root, "espace-a.zip"), new[] { photo });
        var zipB = ArchiveBuilder.WriteComplete(Path.Combine(Root, "espace-b.zip"), new[] { video });

        await using var library = await CreateLibraryAsync();
        var spaceA = await CreateSpaceAsync(library, "Compte A");
        var spaceB = await CreateSpaceAsync(library, "Compte B");

        var resultA = await library.Import.RunAsync(new ImportRequest(spaceA.Id, new[] { zipA }));
        var resultB = await library.Import.RunAsync(new ImportRequest(spaceB.Id, new[] { zipB }));
        Assert.AreEqual(1, resultA.Counters.MemoriesCreated);
        Assert.AreEqual(1, resultB.Counters.MemoriesCreated);
        Assert.AreEqual(0, resultA.Counters.Errors);
        Assert.AreEqual(0, resultB.Counters.Errors);

        var cardsA = await library.Catalog.QueryAsync(new MemoryQuery { SpaceId = spaceA.Id });
        var cardsB = await library.Catalog.QueryAsync(new MemoryQuery { SpaceId = spaceB.Id });
        Assert.AreEqual(1, cardsA.Count);
        Assert.AreEqual(1, cardsB.Count);
        Assert.AreNotEqual(cardsA[0].Id, cardsB[0].Id);
        Assert.AreNotEqual(cardsA[0].SpaceId, cardsB[0].SpaceId);
        Assert.AreEqual(cardsA[0].ExternalKey, cardsB[0].ExternalKey);
        Assert.AreEqual(photo.Id.ToString("D"), cardsA[0].ExternalKey);

        Assert.AreEqual(MediaKind.Photo, cardsA[0].Kind);
        Assert.AreEqual("jpeg", cardsA[0].Format);
        Assert.IsNull(cardsA[0].LocalDate);
        Assert.AreEqual(DateSource.Unknown, cardsA[0].DateSource);

        Assert.AreEqual(MediaKind.Video, cardsB[0].Kind);
        Assert.AreEqual("mp4", cardsB[0].Format);
        Assert.AreEqual(new DateOnly(2022, 3, 4), cardsB[0].LocalDate);
        Assert.AreEqual(DateSource.Mp4CreationTime, cardsB[0].DateSource);
        Assert.AreEqual(6500, cardsB[0].DurationMs);

        Assert.AreEqual(1, await library.Catalog.CountAsync(new MemoryQuery { SpaceId = spaceA.Id }));
        Assert.AreEqual(1, await library.Catalog.CountAsync(new MemoryQuery { SpaceId = spaceB.Id }));
        Assert.AreEqual(1, await library.Catalog.CountAsync(new MemoryQuery { Year = 2022 }));
        Assert.AreEqual(1, await library.Catalog.CountAsync(new MemoryQuery { UndatedOnly = true }));
        Assert.AreEqual(2, await library.Catalog.CountAsync(new MemoryQuery()));

        var detailsA = await library.Catalog.GetDetailsAsync(cardsA[0].Id);
        var detailsB = await library.Catalog.GetDetailsAsync(cardsB[0].Id);
        Assert.IsNotNull(detailsA);
        Assert.IsNotNull(detailsB);
        Assert.AreEqual(1, detailsA.Sources.Count);
        Assert.AreEqual(1, detailsB.Sources.Count);
        Assert.AreEqual(zipA, detailsA.Sources[0].ArchivePath);
        Assert.AreEqual(zipB, detailsB.Sources[0].ArchivePath);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(photo.MainBytes!)).ToLowerInvariant(),
            detailsA.Sources[0].Sha256);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(video.MainBytes!)).ToLowerInvariant(),
            detailsB.Sources[0].Sha256);

        CollectionAssert.AreEqual(
            photo.MainBytes!,
            await File.ReadAllBytesAsync(library.Catalog.GetAssetPath(cardsA[0].MainAssetId!.Value)));
        CollectionAssert.AreEqual(
            video.MainBytes!,
            await File.ReadAllBytesAsync(library.Catalog.GetAssetPath(cardsB[0].MainAssetId!.Value)));

        Assert.AreEqual(0, (await library.Catalog.GetDateCandidatesAsync(cardsA[0].Id)).Count);
        Assert.AreEqual(1, (await library.Catalog.GetDateCandidatesAsync(cardsB[0].Id)).Count);

        var stats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(2, stats.Memories);
        Assert.AreEqual(2, stats.Assets);
        Assert.AreEqual(2, stats.Sources);
        Assert.AreEqual(2, stats.Spaces);
    }
}
