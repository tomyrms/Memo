namespace Memo.Core.Tests;

[TestClass]
public sealed class DiscoveryTests : TestBase
{
    [TestMethod]
    public async Task ThisDay_UsesPriorYears_RespectsExclusions_AndUncertainDates()
    {
        var photos = new[]
        {
            (Uuid: Guid.NewGuid(), Date: new DateOnly(2019, 5, 12), Excluded: false),
            (Uuid: Guid.NewGuid(), Date: new DateOnly(2021, 5, 12), Excluded: false),
            (Uuid: Guid.NewGuid(), Date: new DateOnly(2022, 5, 12), Excluded: true),
        };
        var videoUuid = Guid.NewGuid();
        var undatedUuid = Guid.NewGuid();
        var entries = new List<(string, byte[])>();
        foreach (var photo in photos)
        {
            entries.Add((
                $"memories/{photo.Uuid}-main.jpg",
                MediaFactory.Jpeg(200, 150, photo.Date)));
        }

        entries.Add((
            $"memories/{videoUuid}-main.mp4",
            MediaFactory.Mp4(new DateTimeOffset(2020, 5, 12, 10, 0, 0, TimeSpan.Zero))));
        entries.Add(($"memories/{undatedUuid}-main.png", MediaFactory.Png(100, 100)));
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "days.zip"), null, entries.ToArray());

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var cards = await library.Catalog.QueryAsync(new MemoryQuery());
        var excludedCard = cards.Single(card => card.ExternalKey == photos[2].Uuid.ToString("D"));
        await library.Catalog.SetExcludedAsync(new[] { excludedCard.Id }, true);

        var currentYear = DateOnly.FromDateTime(DateTime.Now).Year;
        var day = new DateOnly(currentYear, 5, 12);
        var certain = await library.Discovery.GetThisDayAsync(
            day, new ThisDayOptions(IncludeUncertainDates: false, IncludeMediaOnly: true, MaxYears: 10));

        CollectionAssert.AreEquivalent(
            new[] { 2021, 2019 },
            certain.Years.ToArray());
        Assert.AreEqual(2, certain.Items.Count);
        Assert.AreEqual(1, certain.UndatedCount);
        Assert.IsFalse(certain.Items.Any(item => item.ExternalKey == photos[2].Uuid.ToString("D")));

        var uncertain = await library.Discovery.GetThisDayAsync(
            day, new ThisDayOptions(IncludeUncertainDates: true, IncludeMediaOnly: true, MaxYears: 10));
        CollectionAssert.AreEquivalent(new[] { 2021, 2020, 2019 }, uncertain.Years.ToArray());
        Assert.AreEqual(3, uncertain.Items.Count);
        Assert.IsTrue(uncertain.Items.Any(item => item.ExternalKey == videoUuid.ToString("D")));
        Assert.IsTrue(uncertain.Items.All(item => item.LocalDate!.Value.Month == 5 && item.LocalDate.Value.Day == 12));

        var withoutMediaOnly = await library.Discovery.GetThisDayAsync(
            day, new ThisDayOptions(IncludeUncertainDates: true, IncludeMediaOnly: false, MaxYears: 10));
        Assert.AreEqual(0, withoutMediaOnly.Items.Count);
    }

    [TestMethod]
    public async Task ThisDay_LeapDay_MatchesExactDateOnly()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "leap.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(200, 150, new DateOnly(2020, 2, 29))));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var leap = await library.Discovery.GetThisDayAsync(
            new DateOnly(2024, 2, 29),
            new ThisDayOptions(IncludeUncertainDates: true, IncludeMediaOnly: true, MaxYears: 10));
        Assert.AreEqual(1, leap.Items.Count);
        CollectionAssert.AreEquivalent(new[] { 2020 }, leap.Years.ToArray());

        var dayBefore = await library.Discovery.GetThisDayAsync(
            new DateOnly(2024, 2, 28),
            new ThisDayOptions(IncludeUncertainDates: true, IncludeMediaOnly: true, MaxYears: 10));
        Assert.AreEqual(0, dayBefore.Items.Count);
        Assert.AreEqual(0, dayBefore.Years.Count);
    }

    [TestMethod]
    public async Task ThisDay_IgnoresPartialDates_UntilPrecisionIsDay()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "partial.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2020, 5, 12))));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var card = (await library.Catalog.QueryAsync(new MemoryQuery()))[0];

        await library.Catalog.SetDateOverrideAsync(
            card.Id, new DateOnly(2021, 5, 12), DatePrecision.Month);
        var monthOnly = await library.Discovery.GetThisDayAsync(
            new DateOnly(2022, 5, 12),
            new ThisDayOptions(IncludeUncertainDates: true, IncludeMediaOnly: true, MaxYears: 10));
        Assert.AreEqual(0, monthOnly.Items.Count);
        Assert.AreEqual(0, (await library.Discovery.SurpriseAsync(5, Array.Empty<long>())).Items.Count);

        await library.Catalog.SetDateOverrideAsync(
            card.Id, new DateOnly(2021, 5, 12), DatePrecision.Day);
        var exact = await library.Discovery.GetThisDayAsync(
            new DateOnly(2022, 5, 12),
            new ThisDayOptions(IncludeUncertainDates: true, IncludeMediaOnly: true, MaxYears: 10));
        Assert.AreEqual(1, exact.Items.Count);
        Assert.AreEqual(new DateOnly(2022, 5, 12), exact.Day);
    }

    [TestMethod]
    public async Task Surprise_PicksOneDay_RecordsHistory_AndAvoidsExcludedIds()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "surprise.zip"), null,
            ($"memories/{first}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2018, 3, 1))),
            ($"memories/{second}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2019, 4, 2))));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var result = await library.Discovery.SurpriseAsync(10, Array.Empty<long>());
        Assert.IsNotNull(result.Day);
        Assert.AreEqual(1, result.Items.Count);
        Assert.IsTrue(result.Items.All(item => item.LocalDate == result.Day));
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Label));

        var recent = await library.Catalog.GetRecentSuggestionIdsAsync(14);
        CollectionAssert.AreEquivalent(
            result.Items.Select(item => item.Id).ToArray(),
            recent.ToArray());

        var secondResult = await library.Discovery.SurpriseAsync(
            10,
            result.Items.Select(item => item.Id).ToArray());
        Assert.IsNotNull(secondResult.Day);
        Assert.AreNotEqual(result.Day, secondResult.Day);
        Assert.AreEqual(1, secondResult.Items.Count);

        var empty = await library.Discovery.SurpriseAsync(0, Array.Empty<long>());
        Assert.AreEqual(0, empty.Items.Count);
        Assert.IsNull(empty.Day);
    }

    [TestMethod]
    public async Task Surprise_ExcludesMemoriesMarkedAsExcluded()
    {
        var excludedUuid = Guid.NewGuid();
        var keptUuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "excluded.zip"), null,
            ($"memories/{excludedUuid}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2017, 6, 7))),
            ($"memories/{keptUuid}-main.jpg", MediaFactory.Jpeg(100, 100, new DateOnly(2016, 6, 7))));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var cards = await library.Catalog.QueryAsync(new MemoryQuery());
        var excluded = cards.Single(card => card.ExternalKey == excludedUuid.ToString("D"));
        await library.Catalog.SetExcludedAsync(new[] { excluded.Id }, true);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = await library.Discovery.SurpriseAsync(5, Array.Empty<long>());
            Assert.IsNotNull(result.Day);
            Assert.AreEqual(new DateOnly(2016, 6, 7), result.Day);
            Assert.AreEqual(1, result.Items.Count);
            Assert.AreEqual(keptUuid.ToString("D"), result.Items[0].ExternalKey);
        }
    }
}
