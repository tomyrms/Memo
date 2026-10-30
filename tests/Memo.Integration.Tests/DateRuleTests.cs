using Memo.Core;

namespace Memo.Integration.Tests;

[TestClass]
public sealed class DateRuleTests : IntegrationTestBase
{
    [TestMethod]
    public async Task LeapDayNearMidnight_FollowsTimeZone_ThisDay_AndManualCorrection()
    {
        // 29 février 2024 à 22:30 UTC : 23:30 à Zurich, déjà le 1er mars à Auckland.
        var video = SyntheticMedia.Video(
            40,
            new DateTimeOffset(2024, 2, 29, 22, 30, 0, TimeSpan.Zero),
            rotation: 90,
            durationMs: 4000);
        var undatedPhoto = SyntheticMedia.Photo(41, "PHOTO SANS DATE", seed: 41);

        // L'horodatage ZIP est volontairement très éloigné : il ne doit jamais
        // devenir une date de souvenir.
        var zip = ArchiveBuilder.WriteComplete(
            Path.Combine(Root, "dates.zip"),
            new[] { video, undatedPhoto },
            new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await using var library = await CreateLibraryAsync("Europe/Zurich");
        var space = await CreateSpaceAsync(library);
        var import = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        Assert.AreEqual(0, import.Counters.Errors);
        Assert.AreEqual(2, import.Counters.MemoriesCreated);

        var cards = await library.Catalog.QueryAsync(new MemoryQuery());
        var videoCard = cards.Single(card => card.ExternalKey == video.Id.ToString("D"));
        var photoCard = cards.Single(card => card.ExternalKey == undatedPhoto.Id.ToString("D"));

        Assert.AreEqual(new DateOnly(2024, 2, 29), videoCard.LocalDate);
        Assert.AreEqual(DateSource.Mp4CreationTime, videoCard.DateSource);
        Assert.AreEqual(DatePrecision.Second, videoCard.DatePrecision);
        Assert.AreEqual(DateMeaning.ContainerCreated, videoCard.DateMeaning);

        Assert.IsNull(photoCard.LocalDate);
        Assert.AreEqual(DateSource.Unknown, photoCard.DateSource);
        Assert.AreEqual(0, (await library.Catalog.GetDateCandidatesAsync(photoCard.Id)).Count);
        Assert.AreEqual(0, await library.Catalog.CountAsync(new MemoryQuery { Year = 1999 }));
        Assert.AreEqual(1, await library.Catalog.CountAsync(new MemoryQuery { UndatedOnly = true }));

        var candidates = await library.Catalog.GetDateCandidatesAsync(videoCard.Id);
        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual(DateSource.Mp4CreationTime, candidates[0].Source);
        Assert.AreEqual("Europe/Zurich", candidates[0].TimeZoneId);
        Assert.AreEqual(new DateOnly(2024, 2, 29), candidates[0].LocalDate);
        Assert.IsNotNull(candidates[0].InstantUtc);
        Assert.IsFalse(candidates[0].RawValue.StartsWith("1999", StringComparison.Ordinal));
        Assert.IsFalse(candidates[0].RawValue.StartsWith("2030", StringComparison.Ordinal));

        var leapDay = await library.Discovery.GetThisDayAsync(
            new DateOnly(2024, 2, 29),
            new ThisDayOptions());
        Assert.AreEqual(1, leapDay.Items.Count);
        Assert.AreEqual(videoCard.Id, leapDay.Items[0].Id);
        Assert.AreEqual(new DateOnly(2024, 2, 29), leapDay.Items[0].LocalDate);
        CollectionAssert.AreEquivalent(new[] { 2024 }, leapDay.Years.ToArray());
        Assert.AreEqual(1, leapDay.UndatedCount);

        var nextDay = await library.Discovery.GetThisDayAsync(
            new DateOnly(2024, 3, 1),
            new ThisDayOptions());
        Assert.AreEqual(0, nextDay.Items.Count);

        // Le changement de fuseau recalcule la date locale : le 29 février devient
        // le 1er mars à Auckland. ThisDay suit la date effective.
        await library.Catalog.RecalculateClassificationAsync("Pacific/Auckland");
        Assert.AreEqual("Pacific/Auckland", library.Info.TimeZoneId);
        Assert.AreEqual("Pacific/Auckland", library.Settings.Current.TimeZoneId);

        var aucklandCard = await library.Catalog.GetCardAsync(videoCard.Id);
        Assert.IsNotNull(aucklandCard);
        Assert.AreEqual(new DateOnly(2024, 3, 1), aucklandCard.LocalDate);
        Assert.AreEqual(DateSource.Mp4CreationTime, aucklandCard.DateSource);

        var aucklandCandidates = await library.Catalog.GetDateCandidatesAsync(videoCard.Id);
        Assert.AreEqual(1, aucklandCandidates.Count);
        Assert.AreEqual("Pacific/Auckland", aucklandCandidates[0].TimeZoneId);
        Assert.AreEqual(new DateOnly(2024, 3, 1), aucklandCandidates[0].LocalDate);

        var aucklandDay = await library.Discovery.GetThisDayAsync(
            new DateOnly(2024, 3, 1),
            new ThisDayOptions());
        Assert.AreEqual(1, aucklandDay.Items.Count);
        Assert.AreEqual(videoCard.Id, aucklandDay.Items[0].Id);
        Assert.AreEqual(
            0,
            (await library.Discovery.GetThisDayAsync(
                new DateOnly(2024, 2, 29),
                new ThisDayOptions())).Items.Count);

        await library.Catalog.RecalculateClassificationAsync("Europe/Zurich");
        var zurichCard = await library.Catalog.GetCardAsync(videoCard.Id);
        Assert.IsNotNull(zurichCard);
        Assert.AreEqual(new DateOnly(2024, 2, 29), zurichCard.LocalDate);
        Assert.AreEqual(
            1,
            (await library.Discovery.GetThisDayAsync(
                new DateOnly(2024, 2, 29),
                new ThisDayOptions())).Items.Count);
        Assert.AreEqual(
            0,
            (await library.Discovery.GetThisDayAsync(
                new DateOnly(2024, 3, 1),
                new ThisDayOptions())).Items.Count);

        // Correction manuelle vers un autre 29 février : la date effective et
        // ThisDay utilisent la correction, pas la date du conteneur.
        await library.Catalog.SetDateOverrideAsync(
            videoCard.Id,
            new DateOnly(2020, 2, 29),
            DatePrecision.Day,
            "anniversaire bissextile");
        var corrected = await library.Catalog.GetCardAsync(videoCard.Id);
        Assert.IsNotNull(corrected);
        Assert.AreEqual(new DateOnly(2020, 2, 29), corrected.LocalDate);
        Assert.AreEqual(DateSource.User, corrected.DateSource);
        Assert.AreEqual(DatePrecision.Day, corrected.DatePrecision);
        Assert.AreEqual(DateMeaning.UserProvided, corrected.DateMeaning);

        var correctedDetails = await library.Catalog.GetDetailsAsync(videoCard.Id);
        Assert.IsNotNull(correctedDetails);
        Assert.IsNotNull(correctedDetails.Override);
        Assert.AreEqual("anniversaire bissextile", correctedDetails.Override.Note);

        var correctedDay = await library.Discovery.GetThisDayAsync(
            new DateOnly(2020, 2, 29),
            new ThisDayOptions());
        Assert.AreEqual(1, correctedDay.Items.Count);
        Assert.AreEqual(new DateOnly(2020, 2, 29), correctedDay.Items[0].LocalDate);
        CollectionAssert.AreEquivalent(new[] { 2020 }, correctedDay.Years.ToArray());

        var olderDay = await library.Discovery.GetThisDayAsync(
            new DateOnly(2019, 3, 1),
            new ThisDayOptions());
        Assert.AreEqual(0, olderDay.Items.Count);

        // Une date partielle (mois seul) reste visible dans les requêtes par année
        // ou par mois, mais ne peut pas déclencher un anniversaire exact.
        await library.Catalog.SetDateOverrideAsync(
            videoCard.Id,
            new DateOnly(2020, 2, 29),
            DatePrecision.Month,
            "mois seul");
        var partial = await library.Catalog.GetCardAsync(videoCard.Id);
        Assert.IsNotNull(partial);
        Assert.AreEqual(DatePrecision.Month, partial.DatePrecision);
        Assert.AreEqual(1, await library.Catalog.CountAsync(new MemoryQuery { Year = 2020 }));
        Assert.AreEqual(1, await library.Catalog.CountAsync(new MemoryQuery { Year = 2020, Month = 2 }));
        Assert.AreEqual(
            0,
            (await library.Discovery.GetThisDayAsync(
                new DateOnly(2020, 2, 29),
                new ThisDayOptions())).Items.Count);

        // Retour à une date exacte puis suppression de la correction : la date du
        // conteneur reprend la main avec le fuseau courant.
        await library.Catalog.SetDateOverrideAsync(
            videoCard.Id,
            new DateOnly(2020, 2, 29),
            DatePrecision.Day,
            "anniversaire");
        Assert.AreEqual(
            1,
            (await library.Discovery.GetThisDayAsync(
                new DateOnly(2020, 2, 29),
                new ThisDayOptions())).Items.Count);

        await library.Catalog.SetDateOverrideAsync(videoCard.Id, null, DatePrecision.Day);
        var restored = await library.Catalog.GetCardAsync(videoCard.Id);
        Assert.IsNotNull(restored);
        Assert.AreEqual(new DateOnly(2024, 2, 29), restored.LocalDate);
        Assert.AreEqual(DateSource.Mp4CreationTime, restored.DateSource);
        Assert.AreEqual(
            0,
            (await library.Discovery.GetThisDayAsync(
                new DateOnly(2020, 2, 29),
                new ThisDayOptions())).Items.Count);
        Assert.AreEqual(
            1,
            (await library.Discovery.GetThisDayAsync(
                new DateOnly(2024, 2, 29),
                new ThisDayOptions())).Items.Count);
        Assert.AreEqual(1, (await library.Discovery.GetThisDayAsync(
            new DateOnly(2024, 2, 29),
            new ThisDayOptions())).UndatedCount);
    }
}
