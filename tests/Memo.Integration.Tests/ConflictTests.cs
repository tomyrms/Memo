using Memo.Core;

namespace Memo.Integration.Tests;

[TestClass]
public sealed class ConflictTests : IntegrationTestBase
{
    [TestMethod]
    public async Task TwoDifferentMains_ForSameUuid_ProduceStableConflict()
    {
        var firstBytes = SyntheticMedia.Photo(30, "CONFLIT A", seed: 30);
        var secondBytes = SyntheticMedia.Photo(30, "CONFLIT B", seed: 31);

        var zipA = ArchiveBuilder.WriteComplete(Path.Combine(Root, "conflit-a.zip"), new[] { firstBytes });
        var zipB = ArchiveBuilder.WriteComplete(Path.Combine(Root, "conflit-b.zip"), new[] { secondBytes });

        await using var library = await CreateLibraryAsync();
        var space = await CreateSpaceAsync(library);

        var import = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zipA, zipB }));
        Assert.IsFalse(import.Cancelled);
        Assert.AreEqual(0, import.Counters.Errors);
        Assert.AreEqual(1, import.Counters.MemoriesCreated);
        Assert.AreEqual(1, import.Counters.MemoriesUpdated);
        Assert.AreEqual(2, import.Counters.MediaImported);

        var card = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        Assert.AreEqual(MemoryStatus.Conflict, card.Status);
        Assert.IsNotNull(card.MainAssetId);
        var principalAssetId = card.MainAssetId.Value;
        var principalBytes = await File.ReadAllBytesAsync(library.Catalog.GetAssetPath(principalAssetId));
        Assert.IsTrue(
            principalBytes.SequenceEqual(firstBytes.MainBytes!) || principalBytes.SequenceEqual(secondBytes.MainBytes!),
            "Le média principal doit être l’une des deux variantes en conflit.");

        var details = await library.Catalog.GetDetailsAsync(card.Id);
        Assert.IsNotNull(details);
        Assert.AreEqual(2, details.Sources.Count(source => source.Role == MediaRole.Main));
        Assert.AreEqual(2, details.Sources.Select(source => source.Sha256).Distinct().Count());

        var stats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(1, stats.Memories);
        Assert.AreEqual(2, stats.Assets);
        Assert.AreEqual(2, stats.Sources);

        var reimport = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zipB, zipA }));
        Assert.IsFalse(reimport.Cancelled);
        Assert.AreEqual(0, reimport.Counters.MemoriesCreated);
        Assert.AreEqual(0, reimport.Counters.MemoriesUpdated);
        Assert.AreEqual(2, reimport.Counters.DuplicatesSkipped);
        Assert.AreEqual(0, reimport.Counters.ArchivesImported);

        var after = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        Assert.AreEqual(MemoryStatus.Conflict, after.Status);
        Assert.AreEqual(principalAssetId, after.MainAssetId);
        CollectionAssert.AreEqual(
            principalBytes,
            await File.ReadAllBytesAsync(library.Catalog.GetAssetPath(after.MainAssetId!.Value)));

        var afterDetails = await library.Catalog.GetDetailsAsync(after.Id);
        Assert.IsNotNull(afterDetails);
        Assert.AreEqual(2, afterDetails.Sources.Count);
        var afterStats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(1, afterStats.Memories);
        Assert.AreEqual(2, afterStats.Assets);
        Assert.AreEqual(2, afterStats.Sources);
    }

    private static MemoryCard AssertSingle(IReadOnlyList<MemoryCard> cards)
    {
        Assert.AreEqual(1, cards.Count);
        return cards[0];
    }
}
