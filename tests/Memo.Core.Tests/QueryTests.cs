namespace Memo.Core.Tests;

[TestClass]
public sealed class QueryTests : TestBase
{
    [TestMethod]
    public async Task Query_FiltersSortsSearchesAndPaginates()
    {
        var uuidA = Guid.NewGuid();
        var uuidB = Guid.NewGuid();
        var uuidC = Guid.NewGuid();
        var uuidD = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "query.zip"), null,
            ($"memories/{uuidA}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2018, 1, 10))),
            ($"memories/{uuidB}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2019, 2, 20))),
            ($"memories/{uuidC}-main.mp4",
                MediaFactory.Mp4(new DateTimeOffset(2020, 3, 30, 12, 0, 0, TimeSpan.Zero))),
            ($"memories/{uuidD}-main.png", MediaFactory.Png(100, 100)));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var cards = (await library.Catalog.QueryAsync(new MemoryQuery())).ToDictionary(card => card.ExternalKey);

        var cardA = cards[uuidA.ToString("D")];
        var cardB = cards[uuidB.ToString("D")];
        var cardC = cards[uuidC.ToString("D")];
        var cardD = cards[uuidD.ToString("D")];

        await library.Catalog.SetFavoriteAsync(new[] { cardB.Id }, true);
        await library.Catalog.SetExcludedAsync(new[] { cardD.Id }, true);
        await library.Catalog.SetLocationAsync(cardA.Id, 48.8566, 2.3522, "Paris");
        await ((MemoCatalog)library.Catalog).SetNoteAsync(cardB.Id, "anniversaire de la plage");
        await library.Analysis.SetOcrTextAsync(cardC.Id, "un panneau bleu", "test");

        Assert.AreEqual(1, (await library.Catalog.QueryAsync(new MemoryQuery { Year = 2020 })).Count);
        Assert.AreEqual(cardC.Id, (await library.Catalog.QueryAsync(new MemoryQuery { Year = 2020 }))[0].Id);
        Assert.AreEqual(1, (await library.Catalog.QueryAsync(new MemoryQuery { Month = 3, Day = 30 })).Count);
        Assert.AreEqual(1, (await library.Catalog.QueryAsync(new MemoryQuery { Kind = MediaKind.Video })).Count);
        Assert.AreEqual(cardB.Id, (await library.Catalog.QueryAsync(new MemoryQuery { FavoritesOnly = true }))[0].Id);
        Assert.AreEqual(cardD.Id, (await library.Catalog.QueryAsync(new MemoryQuery { UndatedOnly = true }))[0].Id);
        Assert.AreEqual(0, (await library.Catalog.QueryAsync(new MemoryQuery { OverlayOnly = true })).Count);
        Assert.AreEqual(cardA.Id, (await library.Catalog.QueryAsync(new MemoryQuery { WithLocationOnly = true }))[0].Id);
        Assert.AreEqual(3, (await library.Catalog.QueryAsync(new MemoryQuery { IncludeExcluded = false })).Count);

        Assert.AreEqual(cardB.Id, (await library.Catalog.QueryAsync(
            new MemoryQuery { SearchText = uuidB.ToString("D") }))[0].Id);
        Assert.AreEqual(cardB.Id, (await library.Catalog.QueryAsync(
            new MemoryQuery { SearchText = "anniversaire" }))[0].Id);
        Assert.AreEqual(cardC.Id, (await library.Catalog.QueryAsync(
            new MemoryQuery { SearchText = "panneau" }))[0].Id);

        var page1 = await library.Catalog.QueryAsync(new MemoryQuery { Limit = 2 });
        var page2 = await library.Catalog.QueryAsync(new MemoryQuery { Limit = 2, Offset = 2 });
        Assert.AreEqual(2, page1.Count);
        Assert.AreEqual(cardC.Id, page1[0].Id);
        Assert.AreEqual(cardB.Id, page1[1].Id);
        Assert.AreEqual(2, page2.Count);
        Assert.AreEqual(cardA.Id, page2[0].Id);
        Assert.AreEqual(cardD.Id, page2[1].Id);
        Assert.AreEqual(4, await library.Catalog.CountAsync(new MemoryQuery()));

        var details = await library.Catalog.GetDetailsAsync(cardB.Id);
        Assert.IsNotNull(details);
        Assert.AreEqual("anniversaire de la plage", details.Notes);
        var detailsA = await library.Catalog.GetDetailsAsync(cardA.Id);
        Assert.IsNotNull(detailsA);
        Assert.AreEqual(48.8566, detailsA.Card.Latitude);
        Assert.AreEqual("Paris", detailsA.Card.LocationLabel);
    }

    [TestMethod]
    public async Task Collections_KeepOrderAndReportDates()
    {
        var uuidA = Guid.NewGuid();
        var uuidB = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "collections.zip"), null,
            ($"memories/{uuidA}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2016, 7, 8))),
            ($"memories/{uuidB}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2017, 8, 9))));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var cards = (await library.Catalog.QueryAsync(new MemoryQuery())).ToDictionary(card => card.ExternalKey);
        var cardA = cards[uuidA.ToString("D")];
        var cardB = cards[uuidB.ToString("D")];

        var album = await library.Catalog.CreateCollectionAsync("Vacances", CollectionKind.Album);
        await library.Catalog.AddToCollectionAsync(album.Id, new[] { cardB.Id, cardA.Id });
        await library.Catalog.ReorderCollectionAsync(album.Id, new[] { cardA.Id, cardB.Id });

        var items = await library.Catalog.GetCollectionItemsAsync(album.Id);
        Assert.AreEqual(2, items.Count);
        Assert.AreEqual(cardA.Id, items[0].Id);
        Assert.AreEqual(cardB.Id, items[1].Id);

        var summaries = await library.Catalog.GetCollectionsAsync(CollectionKind.Album);
        Assert.AreEqual(1, summaries.Count);
        Assert.AreEqual("Vacances", summaries[0].Name);
        Assert.AreEqual(2, summaries[0].ItemCount);
        Assert.AreEqual(new DateOnly(2016, 7, 8), summaries[0].FirstDate);
        Assert.AreEqual(new DateOnly(2017, 8, 9), summaries[0].LastDate);

        await library.Catalog.RemoveFromCollectionAsync(album.Id, new[] { cardA.Id });
        Assert.AreEqual(1, (await library.Catalog.GetCollectionItemsAsync(album.Id)).Count);
        await library.Catalog.RenameCollectionAsync(album.Id, "Vacances 2016");
        Assert.AreEqual("Vacances 2016", (await library.Catalog.GetCollectionsAsync())[0].Name);
        await library.Catalog.DeleteCollectionAsync(album.Id);
        Assert.AreEqual(0, (await library.Catalog.GetCollectionsAsync()).Count);
    }

    [TestMethod]
    public async Task SummariesAndBounds_UseEffectiveDate()
    {
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "summaries.zip"), null,
            ($"memories/{Guid.NewGuid()}-main.jpg", MediaFactory.Jpeg(10, 10, new DateOnly(2018, 1, 10))),
            ($"memories/{Guid.NewGuid()}-main.jpg", MediaFactory.Jpeg(10, 10, new DateOnly(2019, 2, 20))),
            ($"memories/{Guid.NewGuid()}-main.mp4",
                MediaFactory.Mp4(new DateTimeOffset(2020, 3, 30, 12, 0, 0, TimeSpan.Zero))),
            ($"memories/{Guid.NewGuid()}-main.png", MediaFactory.Png(10, 10)));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var years = await library.Catalog.GetYearSummariesAsync();
        Assert.AreEqual(3, years.Count);
        var year2020 = years.Single(summary => summary.Year == 2020);
        Assert.AreEqual(1, year2020.Count);
        Assert.AreEqual(1, year2020.Videos);
        Assert.AreEqual(0, year2020.Photos);

        var months = await library.Catalog.GetMonthSummariesAsync(2020);
        Assert.AreEqual(1, months.Count);
        Assert.AreEqual(3, months[0].Month);
        Assert.AreEqual(new DateOnly(2018, 1, 10), await library.Catalog.GetEarliestDateAsync());
        Assert.AreEqual(new DateOnly(2020, 3, 30), await library.Catalog.GetLatestDateAsync());
    }

    [TestMethod]
    public async Task ThumbnailPath_IsStoredAndResolvedSafely()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "thumb.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2015, 1, 2))));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var card = (await library.Catalog.QueryAsync(new MemoryQuery()))[0];
        PathFactory.WriteFile(library.Info.RootPath, "derived/thumbs/test.bin", new byte[] { 1, 2, 3 });

        await library.Catalog.SetThumbnailAsync(card.Id, "derived/thumbs/test.bin");
        Assert.IsTrue((await library.Catalog.GetCardAsync(card.Id))!.HasThumbnail);
        Assert.IsTrue(library.Catalog.GetThumbnailPath(card.Id)!.EndsWith("test.bin", StringComparison.Ordinal));

        await library.Catalog.SetThumbnailAsync(card.Id, null);
        Assert.IsNull(library.Catalog.GetThumbnailPath(card.Id));
        Assert.IsFalse((await library.Catalog.GetCardAsync(card.Id))!.HasThumbnail);

        await ThrowsAsync<ArgumentException>(() =>
            library.Catalog.SetThumbnailAsync(card.Id, "../outside.jpg"));
    }
}
