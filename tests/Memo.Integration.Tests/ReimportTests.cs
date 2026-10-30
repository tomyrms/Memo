using Memo.Core;
using Memo.TestSupport;

namespace Memo.Integration.Tests;

[TestClass]
public sealed class ReimportTests : IntegrationTestBase
{
    [TestMethod]
    public async Task SameBatchTwice_ThenComplement_KeepsPersonalizationWithoutDuplicates()
    {
        var first = SyntheticMedia.Photo(10, "PREMIER SOUVENIR", seed: 10);
        var second = SyntheticMedia.Photo(11, "SOUVENIR COMPLEMENTAIRE", seed: 11);
        var firstWithOverlay = first.WithOverlay(caption: "CALQUE PREMIER", seed: 10);

        var archive = ArchiveBuilder.WriteComplete(
            Path.Combine(Root, "lot-initial.zip"),
            new[] { first });

        // Le lot complémentaire apporte le calque du premier souvenir et un
        // second souvenir complet, dans une seule archive.
        var complement = ExportBuilder.Write(
            Path.Combine(Root, "lot-complement.zip"),
            new[] { firstWithOverlay, second },
            new ExportArchiveOptions
            {
                Roles = SyntheticRoleFilter.Overlay,
                IncludeIndexHtml = true,
                IncludeJson = true,
                ExtraEntries = new[]
                {
                    new ZipEntrySpec(second.MainEntryName!, second.MainBytes!),
                },
            });

        await using var library = await CreateLibraryAsync();
        var space = await CreateSpaceAsync(library);

        var initial = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { archive }));
        Assert.AreEqual(1, initial.Counters.MemoriesCreated);

        var firstCard = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        Assert.AreEqual(MemoryStatus.MediaOnly, firstCard.Status);
        Assert.AreEqual(first.Id.ToString("D"), firstCard.ExternalKey);

        var album = await library.Catalog.CreateCollectionAsync("Album personnel", CollectionKind.Album);
        await library.Catalog.SetFavoriteAsync(new[] { firstCard.Id }, true);
        await library.Catalog.AddToCollectionAsync(album.Id, new[] { firstCard.Id });
        await library.Catalog.SetDateOverrideAsync(
            firstCard.Id,
            new DateOnly(2019, 12, 31),
            DatePrecision.Day,
            "date corrigée");

        var repeated = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { archive }));
        Assert.IsFalse(repeated.Cancelled);
        Assert.AreEqual(0, repeated.Counters.MemoriesCreated);
        Assert.AreEqual(0, repeated.Counters.MemoriesUpdated);
        Assert.AreEqual(1, repeated.Counters.DuplicatesSkipped);
        Assert.AreEqual(0, repeated.Counters.ArchivesImported);
        Assert.IsTrue(
            repeated.Diagnostics.Any(diagnostic => diagnostic.Code == "archive.duplicate"),
            "Le réimport du même lot doit être signalé comme doublon.");

        var afterRepeat = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        Assert.IsTrue(afterRepeat.IsFavorite);
        Assert.AreEqual(new DateOnly(2019, 12, 31), afterRepeat.LocalDate);
        Assert.AreEqual(DateSource.User, afterRepeat.DateSource);
        Assert.AreEqual(1, (await library.Catalog.GetCollectionItemsAsync(album.Id)).Count);

        var statsAfterRepeat = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(1, statsAfterRepeat.Memories);
        Assert.AreEqual(1, statsAfterRepeat.Assets);
        Assert.AreEqual(1, statsAfterRepeat.Sources);

        var complemented = await library.Import.RunAsync(
            new ImportRequest(space.Id, new[] { complement }));
        Assert.IsFalse(complemented.Cancelled);
        Assert.AreEqual(0, complemented.Counters.Errors);
        Assert.AreEqual(1, complemented.Counters.ArchivesImported);
        Assert.AreEqual(1, complemented.Counters.MediaImported);
        Assert.AreEqual(1, complemented.Counters.OverlaysImported);
        Assert.AreEqual(1, complemented.Counters.MemoriesCreated);
        Assert.AreEqual(1, complemented.Counters.MemoriesUpdated);

        var cards = await library.Catalog.QueryAsync(new MemoryQuery());
        Assert.AreEqual(2, cards.Count);
        var byKey = cards.ToDictionary(card => card.ExternalKey, StringComparer.Ordinal);
        var personalized = byKey[first.Id.ToString("D")];
        var added = byKey[second.Id.ToString("D")];

        Assert.AreEqual(MemoryStatus.Complete, personalized.Status);
        Assert.AreEqual(1, personalized.OverlayCount);
        Assert.IsTrue(personalized.IsFavorite);
        Assert.AreEqual(new DateOnly(2019, 12, 31), personalized.LocalDate);
        Assert.AreEqual(DateSource.User, personalized.DateSource);
        Assert.AreEqual(DatePrecision.Day, personalized.DatePrecision);

        var personalizedDetails = await library.Catalog.GetDetailsAsync(personalized.Id);
        Assert.IsNotNull(personalizedDetails);
        Assert.IsNotNull(personalizedDetails.Override);
        Assert.AreEqual("date corrigée", personalizedDetails.Override.Note);
        Assert.AreEqual(2, personalizedDetails.Sources.Count);
        Assert.AreEqual(1, personalizedDetails.Collections.Count);
        Assert.AreEqual("Album personnel", personalizedDetails.Collections[0].Name);

        Assert.AreEqual(MemoryStatus.MediaOnly, added.Status);
        Assert.IsFalse(added.IsFavorite);
        Assert.IsNull(added.LocalDate);

        var stats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(2, stats.Memories);
        Assert.AreEqual(3, stats.Assets);
        Assert.AreEqual(3, stats.Sources);
        Assert.AreEqual(1, (await library.Catalog.GetCollectionItemsAsync(album.Id)).Count);

        var repeatedComplement = await library.Import.RunAsync(
            new ImportRequest(space.Id, new[] { complement }));
        Assert.AreEqual(0, repeatedComplement.Counters.MemoriesCreated);
        Assert.AreEqual(0, repeatedComplement.Counters.MemoriesUpdated);
        Assert.AreEqual(1, repeatedComplement.Counters.DuplicatesSkipped);

        var finalStats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(2, finalStats.Memories);
        Assert.AreEqual(3, finalStats.Assets);
        Assert.AreEqual(3, finalStats.Sources);
        var finalCard = await library.Catalog.GetCardAsync(personalized.Id);
        Assert.IsNotNull(finalCard);
        Assert.IsTrue(finalCard.IsFavorite);
        Assert.AreEqual(new DateOnly(2019, 12, 31), finalCard.LocalDate);
        Assert.AreEqual(
            1,
            (await library.Catalog.QueryAsync(
                new MemoryQuery { SearchText = first.Id.ToString("D") })).Count);
    }

    private static MemoryCard AssertSingle(IReadOnlyList<MemoryCard> cards)
    {
        Assert.AreEqual(1, cards.Count);
        return cards[0];
    }
}
