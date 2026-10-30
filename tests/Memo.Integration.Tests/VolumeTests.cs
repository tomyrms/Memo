using Memo.Core;
using Memo.TestSupport;

namespace Memo.Integration.Tests;

[TestClass]
public sealed class VolumeTests : IntegrationTestBase
{
    [TestMethod]
    public async Task OneHundredFiftyMemories_ImportWithExactCounts_AndConsistentPaging()
    {
        const int memoryCount = 150;
        var memories = new List<SyntheticMemory>(memoryCount);
        for (var index = 0; index < memoryCount; index++)
        {
            var memory = SyntheticMedia.Photo(
                index,
                $"SOUVENIR {index:000}",
                width: 320,
                height: 240,
                seed: index + 1);
            if (index % 10 == 0)
            {
                memory = memory.WithOverlay(
                    width: 320,
                    height: 240,
                    caption: $"CALQUE {index:000}",
                    seed: index + 1);
            }

            memories.Add(memory);
        }

        var archive = ArchiveBuilder.WriteComplete(
            Path.Combine(Root, "volume-150.zip"),
            memories);

        await using var library = await CreateLibraryAsync();
        var space = await CreateSpaceAsync(library);
        var result = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { archive }));

        Assert.IsFalse(result.Cancelled);
        Assert.AreEqual(0, result.Counters.Errors);
        Assert.AreEqual(1, result.Counters.ArchivesSeen);
        Assert.AreEqual(1, result.Counters.ArchivesImported);
        Assert.AreEqual(memoryCount + 15 + 2, result.Counters.EntriesSeen);
        Assert.AreEqual(memoryCount, result.Counters.MediaImported);
        Assert.AreEqual(15, result.Counters.OverlaysImported);
        Assert.AreEqual(memoryCount, result.Counters.MemoriesCreated);
        Assert.AreEqual(15, result.Counters.MemoriesUpdated);
        Assert.AreEqual(0, result.Counters.DuplicatesSkipped);
        Assert.AreEqual(0, result.Counters.Unrecognized);

        var stats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(memoryCount, stats.Memories);
        Assert.AreEqual(memoryCount + 15, stats.Assets);
        Assert.AreEqual(memoryCount + 15, stats.Sources);
        Assert.AreEqual(memoryCount, await library.Catalog.CountAsync(new MemoryQuery()));

        var all = await library.Catalog.QueryAsync(new MemoryQuery { Limit = 100_000 });
        Assert.AreEqual(memoryCount, all.Count);
        Assert.AreEqual(
            memoryCount,
            all.Select(card => card.ExternalKey).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(15, all.Count(card => card.Status == MemoryStatus.Complete));
        Assert.AreEqual(135, all.Count(card => card.Status == MemoryStatus.MediaOnly));
        Assert.AreEqual(15, all.Count(card => card.OverlayCount == 1));
        Assert.AreEqual(135, all.Count(card => card.OverlayCount == 0));

        const int pageSize = 37;
        var paged = new List<MemoryCard>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await library.Catalog.QueryAsync(
                new MemoryQuery { Offset = offset, Limit = pageSize });
            if (page.Count == 0)
            {
                break;
            }

            Assert.IsTrue(page.Count <= pageSize);
            paged.AddRange(page);
        }

        Assert.AreEqual(memoryCount, paged.Count);
        CollectionAssert.AreEqual(
            all.Select(card => card.Id).ToArray(),
            paged.Select(card => card.Id).ToArray());
        Assert.AreEqual(
            0,
            (await library.Catalog.QueryAsync(
                new MemoryQuery { Offset = memoryCount, Limit = pageSize })).Count);

        var ascending = await library.Catalog.QueryAsync(
            new MemoryQuery { Descending = false, Limit = 100_000 });
        Assert.AreEqual(memoryCount, ascending.Count);
        CollectionAssert.AreEqual(
            all.Select(card => card.Id).Reverse().ToArray(),
            ascending.Select(card => card.Id).ToArray());
    }
}
