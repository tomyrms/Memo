using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Memo.Core.Tests;

[TestClass]
public sealed class ImportTests : TestBase
{
    [TestMethod]
    public async Task MediaAndOverlay_AcrossTwoZips_AreAssociated_InAnyOrder()
    {
        var uuid = Guid.NewGuid();
        var jpeg = MediaFactory.Jpeg(400, 300, new DateOnly(2021, 4, 5));
        var overlay = MediaFactory.Png(400, 300);
        var zipMedia = await ZipFactory.CreateAsync(
            Path.Combine(Root, "media.zip"), null,
            ($"memories/{uuid}-main.jpg", jpeg));
        var zipOverlay = await ZipFactory.CreateAsync(
            Path.Combine(Root, "overlay.zip"), null,
            ($"memories/{uuid}-overlay.png", overlay));

        await using var direct = await CreateLibraryAsync();
        var directSpace = await NewSpaceAsync(direct);
        await direct.Import.RunAsync(new ImportRequest(directSpace.Id, new[] { zipMedia, zipOverlay }));
        var directCard = AssertSingle(await direct.Catalog.QueryAsync(new MemoryQuery()));

        await using var reversed = await CreateLibraryAsync();
        var reversedSpace = await NewSpaceAsync(reversed);
        await reversed.Import.RunAsync(new ImportRequest(reversedSpace.Id, new[] { zipOverlay, zipMedia }));
        var reversedCard = AssertSingle(await reversed.Catalog.QueryAsync(new MemoryQuery()));

        Assert.AreEqual(MemoryStatus.Complete, directCard.Status);
        Assert.AreEqual(MemoryStatus.Complete, reversedCard.Status);
        Assert.AreEqual(directCard.LocalDate, reversedCard.LocalDate);
        Assert.AreEqual(directCard.MainAssetId is not null, reversedCard.MainAssetId is not null);
        Assert.AreEqual(directCard.OverlayAssetId is not null, reversedCard.OverlayAssetId is not null);
        Assert.AreEqual(1, directCard.OverlayCount);

        var details = await direct.Catalog.GetDetailsAsync(directCard.Id);
        Assert.IsNotNull(details);
        Assert.AreEqual(2, details.Sources.Count);
        Assert.IsTrue(details.Sources.Any(source => source.Role == MediaRole.Main));
        Assert.IsTrue(details.Sources.Any(source => source.Role == MediaRole.Overlay));
        Assert.IsTrue(File.Exists(direct.Catalog.GetAssetPath(directCard.MainAssetId!.Value)));
        Assert.IsTrue(File.Exists(direct.Catalog.GetAssetPath(directCard.OverlayAssetId!.Value)));
        Assert.IsTrue(File.Exists(reversed.Catalog.GetAssetPath(reversedCard.MainAssetId!.Value)));
        Assert.IsTrue(File.Exists(reversed.Catalog.GetAssetPath(reversedCard.OverlayAssetId!.Value)));
        Assert.AreEqual(
            Path.GetFileName(direct.Catalog.GetAssetPath(directCard.MainAssetId.Value)),
            Path.GetFileName(reversed.Catalog.GetAssetPath(reversedCard.MainAssetId.Value)));
    }

    [TestMethod]
    public async Task Reimport_OfSameZip_IsIdempotent_AndPreservesPersonalization()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "same.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(320, 240, new DateOnly(2020, 5, 1))));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        var first = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var card = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        await library.Catalog.SetFavoriteAsync(new[] { card.Id }, true);
        await library.Catalog.SetDateOverrideAsync(card.Id, new DateOnly(2019, 12, 31), DatePrecision.Day, "corrigée");

        var second = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var after = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        var stats = await library.Catalog.GetStorageStatsAsync();

        Assert.AreEqual(1, first.Counters.MemoriesCreated);
        Assert.AreEqual(0, second.Counters.MemoriesCreated);
        Assert.AreEqual(0, second.Counters.MemoriesUpdated);
        Assert.IsTrue(second.Diagnostics.Any(diagnostic => diagnostic.Code == "archive.duplicate"));
        Assert.AreEqual(1, stats.Memories);
        Assert.AreEqual(1, stats.Assets);
        Assert.AreEqual(1, stats.Sources);
        Assert.IsTrue(after.IsFavorite);
        Assert.AreEqual(new DateOnly(2019, 12, 31), after.LocalDate);
        Assert.AreEqual(DateSource.User, after.DateSource);
    }

    [TestMethod]
    public async Task SourceSpaces_KeepSameUuidSeparate_AndShareBytes()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "shared.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(100, 80, new DateOnly(2018, 2, 3))));

        await using var library = await CreateLibraryAsync();
        var firstSpace = await NewSpaceAsync(library, "Compte A");
        var secondSpace = await NewSpaceAsync(library, "Compte B");
        await library.Import.RunAsync(new ImportRequest(firstSpace.Id, new[] { zip }));
        await library.Import.RunAsync(new ImportRequest(secondSpace.Id, new[] { zip }));

        var cards = await library.Catalog.QueryAsync(new MemoryQuery());
        var stats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(2, cards.Count);
        Assert.AreNotEqual(cards[0].Id, cards[1].Id);
        Assert.AreNotEqual(cards[0].SpaceId, cards[1].SpaceId);
        Assert.AreEqual(1, stats.Assets);
        Assert.AreEqual(2, stats.Sources);
        Assert.AreEqual(
            library.Catalog.GetAssetPath(cards[0].MainAssetId!.Value),
            library.Catalog.GetAssetPath(cards[1].MainAssetId!.Value));
    }

    [TestMethod]
    public async Task OverlayAlone_IsOverlayOnly_WithoutMainMedia()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "overlay-only.zip"), null,
            ($"memories/{uuid}-overlay.png", MediaFactory.Png(1080, 1920)));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var card = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        Assert.AreEqual(MemoryStatus.OverlayOnly, card.Status);
        Assert.IsNull(card.MainAssetId);
        Assert.IsNotNull(card.OverlayAssetId);
        Assert.AreEqual(MediaKind.Unknown, card.Kind);
        Assert.IsNull(card.LocalDate);
        Assert.AreEqual(1, (await library.Catalog.QueryAsync(new MemoryQuery { OverlayOnly = true })).Count);
    }

    [TestMethod]
    public async Task PhotoWithoutDate_IsVisibleAsUndated()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "undated.zip"), null,
            ($"memories/{uuid}-main.png", MediaFactory.Png(320, 200)));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var card = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        Assert.IsNull(card.LocalDate);
        Assert.AreEqual(DateSource.Unknown, card.DateSource);
        Assert.AreEqual(DatePrecision.Unknown, card.DatePrecision);
        Assert.AreEqual(1, (await library.Catalog.QueryAsync(new MemoryQuery { UndatedOnly = true })).Count);
        Assert.AreEqual(0, (await library.Catalog.QueryAsync(new MemoryQuery { Year = 2020 })).Count);
        Assert.AreEqual(0, (await library.Catalog.QueryAsync(new MemoryQuery { IncludeUndated = false })).Count);
    }

    [TestMethod]
    public async Task Mp4CreationTime_IsConvertedToLocalDate_AndZipDateIsIgnored()
    {
        var uuid = Guid.NewGuid();
        var creation = new DateTimeOffset(2023, 6, 15, 22, 30, 0, TimeSpan.Zero);
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "video.zip"),
            new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ($"memories/{uuid}-main.mp4", MediaFactory.Mp4(creation, 1920, 1080, 90, 3000, true)));

        await using var library = await CreateLibraryAsync("Europe/Paris");
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var card = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        Assert.AreEqual(new DateOnly(2023, 6, 16), card.LocalDate);
        Assert.AreEqual(DateSource.Mp4CreationTime, card.DateSource);
        Assert.AreEqual(DateMeaning.ContainerCreated, card.DateMeaning);
        Assert.AreEqual(DatePrecision.Second, card.DatePrecision);
        Assert.AreEqual(MediaKind.Video, card.Kind);
        Assert.AreEqual(3000, card.DurationMs);
        Assert.AreEqual(1920, card.Width);
        Assert.AreEqual(1080, card.Height);

        var candidates = await library.Catalog.GetDateCandidatesAsync(card.Id);
        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("Europe/Paris", candidates[0].TimeZoneId);
        Assert.IsNotNull(candidates[0].InstantUtc);
        Assert.IsFalse(candidates[0].RawValue.StartsWith("2001", StringComparison.Ordinal));
        Assert.AreEqual(0.7, candidates[0].Confidence);

        var media = await library.Catalog.GetMediaInfoAsync(card.Id, MediaRole.Main);
        Assert.IsNotNull(media);
        Assert.AreEqual("mp4", media.Format);
        Assert.AreEqual(90, media.Rotation);
        Assert.IsTrue(media.HasAudio);
        Assert.AreEqual(0, (await library.Catalog.QueryAsync(new MemoryQuery { Year = 2001 })).Count);
        Assert.AreEqual(1, (await library.Catalog.QueryAsync(new MemoryQuery { Year = 2023, Month = 6, Day = 16 })).Count);
    }

    [TestMethod]
    public async Task TimeZoneChangeNearMidnight_RecalculatesLocalDate_AndPersists()
    {
        var root = NewDirectory();
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "midnight.zip"), null,
            ($"memories/{uuid}-main.mp4",
                MediaFactory.Mp4(new DateTimeOffset(2023, 1, 2, 0, 30, 0, TimeSpan.Zero))));

        await using (var library = await MemoLibrary.CreateAsync(root, "Fuseau", "Europe/Paris"))
        {
            var space = await NewSpaceAsync(library);
            await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
            var card = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
            Assert.AreEqual(new DateOnly(2023, 1, 2), card.LocalDate);

            await library.Catalog.RecalculateClassificationAsync("America/New_York");
            var recalculated = await library.Catalog.GetCardAsync(card.Id);
            Assert.IsNotNull(recalculated);
            Assert.AreEqual(new DateOnly(2023, 1, 1), recalculated.LocalDate);
            Assert.AreEqual("America/New_York", library.Info.TimeZoneId);
            Assert.AreEqual("America/New_York", library.Settings.Current.TimeZoneId);

            var candidates = await library.Catalog.GetDateCandidatesAsync(card.Id);
            Assert.AreEqual("America/New_York", candidates[0].TimeZoneId);
        }

        await using var reopened = await MemoLibrary.OpenAsync(root);
        Assert.AreEqual("America/New_York", reopened.Info.TimeZoneId);
        var reopenedCard = AssertSingle(await reopened.Catalog.QueryAsync(new MemoryQuery()));
        Assert.AreEqual(new DateOnly(2023, 1, 1), reopenedCard.LocalDate);
    }

    [TestMethod]
    public async Task DateOverride_WinsAndSurvivesAnotherImport_ThenCanBeRemoved()
    {
        var uuid = Guid.NewGuid();
        var jpeg = MediaFactory.Jpeg(300, 200, new DateOnly(2020, 5, 1));
        var firstZip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "first.zip"), null,
            ($"memories/{uuid}-main.jpg", jpeg));
        var secondZip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "second.zip"), null,
            ($"memories/{uuid}-main.jpg", jpeg));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { firstZip }));
        var card = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        await library.Catalog.SetDateOverrideAsync(card.Id, new DateOnly(2019, 12, 31), DatePrecision.Day, "anniversaire");

        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { secondZip }));
        var afterImport = await library.Catalog.GetCardAsync(card.Id);
        Assert.IsNotNull(afterImport);
        Assert.AreEqual(new DateOnly(2019, 12, 31), afterImport.LocalDate);
        Assert.AreEqual(DateSource.User, afterImport.DateSource);
        Assert.AreEqual(DateMeaning.UserProvided, afterImport.DateMeaning);
        var details = await library.Catalog.GetDetailsAsync(card.Id);
        Assert.IsNotNull(details);
        Assert.IsNotNull(details.Override);
        Assert.AreEqual("anniversaire", details.Override.Note);
        Assert.IsTrue(details.DateCandidates.Count >= 1);

        await library.Catalog.SetDateOverrideAsync(card.Id, null, DatePrecision.Day);
        var restored = await library.Catalog.GetCardAsync(card.Id);
        Assert.IsNotNull(restored);
        Assert.AreEqual(new DateOnly(2020, 5, 1), restored.LocalDate);
        Assert.AreEqual(DateSource.ExifDateTimeOriginal, restored.DateSource);
    }

    [TestMethod]
    public async Task IdenticalFilesUnderTwoUuids_AreTwoMemoriesWithSharedBytes()
    {
        var bytes = MediaFactory.Jpeg(200, 150, new DateOnly(2022, 8, 9));
        var uuidA = Guid.NewGuid();
        var uuidB = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "twins.zip"), null,
            ($"memories/{uuidA}-main.jpg", bytes),
            ($"memories/{uuidB}-main.jpg", bytes));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var cards = await library.Catalog.QueryAsync(new MemoryQuery());
        var stats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(2, cards.Count);
        Assert.AreEqual(2, stats.Memories);
        Assert.AreEqual(1, stats.Assets);
        Assert.AreEqual(2, stats.Sources);
        Assert.AreEqual(cards[0].MainAssetId, cards[1].MainAssetId);
        var mediaFiles = Directory
            .EnumerateFiles(Path.Combine(library.Info.RootPath, "media"), "*", SearchOption.AllDirectories)
            .ToList();
        Assert.AreEqual(1, mediaFiles.Count);
    }

    [TestMethod]
    public async Task TwoDifferentMains_ProduceConflict_WithDeterministicPrincipal()
    {
        var uuid = Guid.NewGuid();
        var firstBytes = MediaFactory.Jpeg(320, 240, new DateOnly(2020, 1, 1));
        var secondBytes = MediaFactory.Jpeg(640, 480, new DateOnly(2021, 2, 2));
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "conflict.zip"), null,
            ($"memories/{uuid}-main.jpg", firstBytes),
            ($"memories/{uuid}-main.png", secondBytes));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var card = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        Assert.AreEqual(MemoryStatus.Conflict, card.Status);
        Assert.IsNotNull(card.MainAssetId);
        var principal = await File.ReadAllBytesAsync(
            library.Catalog.GetAssetPath(card.MainAssetId!.Value));
        CollectionAssert.AreEqual(firstBytes, principal);

        var details = await library.Catalog.GetDetailsAsync(card.Id);
        Assert.IsNotNull(details);
        Assert.AreEqual(2, details.Sources.Count(source => source.Role == MediaRole.Main));
        Assert.AreEqual(2, (await library.Catalog.GetStorageStatsAsync()).Assets);
    }

    [TestMethod]
    public async Task ImportProgress_ReportsRealTotalsAndCounters()
    {
        var uuidA = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "progress.zip"), null,
            ($"memories/{uuidA}-main.jpg", MediaFactory.Jpeg()),
            ($"memories/{uuidA}-overlay.png", MediaFactory.Png()));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        var reports = new List<ImportProgress>();
        var progress = new InlineProgress<ImportProgress>(report =>
        {
            lock (reports)
            {
                reports.Add(report);
            }
        });

        var result = await library.Import.RunAsync(
            new ImportRequest(space.Id, new[] { zip }), progress);

        Assert.IsFalse(result.Cancelled);
        Assert.IsTrue(reports.Count > 0);
        Assert.IsTrue(reports.Any(report => report.Phase == ImportPhase.Done));
        var done = reports.Last(report => report.Phase == ImportPhase.Done);
        Assert.AreEqual(2, done.FilesTotal);
        Assert.AreEqual(2, done.FilesProcessed);
        Assert.AreEqual(1, done.Counters.ArchivesSeen);
        Assert.AreEqual(1, done.Counters.ArchivesImported);
        Assert.AreEqual(2, done.Counters.EntriesSeen);
        Assert.AreEqual(1, done.Counters.MediaImported);
        Assert.AreEqual(1, done.Counters.OverlaysImported);
        Assert.AreEqual(1, done.Counters.MemoriesCreated);
    }

    [TestMethod]
    public async Task InterruptedArchive_CanBeResumedWithoutDuplicates()
    {
        var uuidA = Guid.NewGuid();
        var uuidB = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "resume.zip"), null,
            ($"memories/{uuidA}-main.jpg", MediaFactory.Jpeg(320, 240)),
            ($"memories/{uuidB}-main.jpg", MediaFactory.Jpeg(321, 241)));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zip))).ToLowerInvariant();
        var lookup = await ((MemoCatalog)library.Catalog).FindArchiveAsync(space.Id, sha);
        Assert.IsNotNull(lookup);
        // Simule un lot interrompu : l'archive reste en cours et doit être reprise.
        await ((MemoCatalog)library.Catalog).SetArchiveStatusAsync(lookup.Id, "processing");

        var resumed = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var stats = await library.Catalog.GetStorageStatsAsync();
        Assert.IsFalse(resumed.Cancelled);
        Assert.AreEqual(0, resumed.Counters.MemoriesCreated);
        Assert.AreEqual(2, stats.Memories);
        Assert.AreEqual(2, stats.Assets);
        Assert.AreEqual(2, stats.Sources);
        Assert.AreEqual(2, (await library.Catalog.QueryAsync(new MemoryQuery())).Count);
    }

    [TestMethod]
    public async Task CorruptZip_ProducesDiagnosticWithoutThrowing()
    {
        var path = Path.Combine(Root, "corrupt.zip");
        await File.WriteAllBytesAsync(
            path,
            new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x11, 0x22, 0x33, 0x44 });

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        var result = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { path }));

        Assert.IsFalse(result.Cancelled);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic =>
            diagnostic.Severity == "error" && diagnostic.Code == "archive.corrupt"));
        Assert.IsTrue(result.Counters.Errors > 0);
        var batches = await library.Catalog.GetBatchesAsync();
        Assert.AreEqual("failed", batches[0].Status);
        Assert.AreEqual(0, (await library.Catalog.QueryAsync(new MemoryQuery())).Count);
    }

    [TestMethod]
    public async Task EntryPathWithParentTraversal_IsRejected()
    {
        var badUuid = Guid.NewGuid();
        var goodUuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "traversal.zip"), null,
            ($"../memories/{badUuid}-main.jpg", MediaFactory.Jpeg()),
            ($"memories/{goodUuid}-main.jpg", MediaFactory.Jpeg()));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        var result = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        Assert.IsTrue(result.Diagnostics.Any(diagnostic =>
            diagnostic.Code == "entry.invalid-path" &&
            diagnostic.EntryPath is not null &&
            diagnostic.EntryPath.Contains("..", StringComparison.Ordinal)));
        var cards = await library.Catalog.QueryAsync(new MemoryQuery());
        Assert.AreEqual(1, cards.Count);
        Assert.AreEqual(goodUuid.ToString("D"), cards[0].ExternalKey);
        var bad = await library.Catalog.QueryAsync(new MemoryQuery { SearchText = badUuid.ToString("D") });
        Assert.AreEqual(0, bad.Count);
    }

    [TestMethod]
    public async Task WebpNamedPng_IsDetectedFromSignature_AndStoredAsWebp()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "mislabelled.zip"), null,
            ($"memories/{uuid}-main.png", MediaFactory.Webp(300, 500)));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));

        var card = AssertSingle(await library.Catalog.QueryAsync(new MemoryQuery()));
        Assert.AreEqual("webp", card.Format);
        Assert.AreEqual(300, card.Width);
        Assert.AreEqual(500, card.Height);
        var details = await library.Catalog.GetDetailsAsync(card.Id);
        Assert.IsNotNull(details);
        Assert.AreEqual("png", details.Sources[0].DeclaredExtension);
        Assert.AreEqual("webp", details.Sources[0].DetectedFormat);
        Assert.IsTrue(library.Catalog.GetAssetPath(card.MainAssetId!.Value).EndsWith(".webp", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExtractedExport_AndFolderOfZips_AreSupported()
    {
        var extractedUuid = Guid.NewGuid();
        var exportRoot = Path.Combine(Root, "export");
        PathFactory.WriteFile(
            exportRoot,
            $"memories/{extractedUuid}-main.jpg",
            MediaFactory.Jpeg(100, 100, new DateOnly(2017, 3, 4)));

        await using var firstLibrary = await CreateLibraryAsync();
        var firstSpace = await NewSpaceAsync(firstLibrary);
        Assert.AreEqual(1, await firstLibrary.Import.InspectAsync(new[] { exportRoot }));
        var extractedImport = await firstLibrary.Import.RunAsync(
            new ImportRequest(firstSpace.Id, new[] { exportRoot }));
        Assert.AreEqual(1, extractedImport.Counters.MemoriesCreated);
        var extractedCard = AssertSingle(await firstLibrary.Catalog.QueryAsync(new MemoryQuery()));
        Assert.AreEqual(new DateOnly(2017, 3, 4), extractedCard.LocalDate);

        var again = await firstLibrary.Import.RunAsync(new ImportRequest(firstSpace.Id, new[] { exportRoot }));
        Assert.AreEqual(0, again.Counters.MemoriesCreated);
        Assert.IsTrue(again.Diagnostics.Any(diagnostic => diagnostic.Code == "archive.duplicate"));

        var zipFolder = Path.Combine(Root, "zips");
        Directory.CreateDirectory(zipFolder);
        for (var index = 0; index < 2; index++)
        {
            await ZipFactory.CreateAsync(
                Path.Combine(zipFolder, $"part-{index}.zip"), null,
                ($"memories/{Guid.NewGuid()}-main.jpg", MediaFactory.Jpeg(50 + index, 50)));
        }

        await using var secondLibrary = await CreateLibraryAsync();
        var secondSpace = await NewSpaceAsync(secondLibrary);
        Assert.AreEqual(2, await secondLibrary.Import.InspectAsync(new[] { zipFolder }));
        var folderImport = await secondLibrary.Import.RunAsync(
            new ImportRequest(secondSpace.Id, new[] { zipFolder }));
        Assert.AreEqual(2, folderImport.Counters.ArchivesSeen);
        Assert.AreEqual(2, folderImport.Counters.ArchivesImported);
        Assert.AreEqual(2, (await secondLibrary.Catalog.QueryAsync(new MemoryQuery())).Count);
    }

    [TestMethod]
    public async Task ChatMedia_IsIgnoredByDefault_AndNeverLinkedWhenIncluded()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "with-chat.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(120, 90)),
            ("chat_media/conversation.jpg", MediaFactory.Jpeg(60, 40)));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var defaultStats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(1, defaultStats.Sources);
        Assert.AreEqual(1, (await library.Catalog.QueryAsync(new MemoryQuery())).Count);

        // Réimporter avec l'option ajoute les médias de conversation sans les rattacher.
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }, IncludeChatMedia: true));
        var includedStats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(2, includedStats.Sources);
        var cards = await library.Catalog.QueryAsync(new MemoryQuery());
        Assert.AreEqual(1, cards.Count);
        var details = await library.Catalog.GetDetailsAsync(cards[0].Id);
        Assert.IsNotNull(details);
        Assert.AreEqual(1, details.Sources.Count);

        var otherSpace = await NewSpaceAsync(library, "Compte avec conversations");
        await library.Import.RunAsync(new ImportRequest(otherSpace.Id, new[] { zip }, IncludeChatMedia: true));
        var otherStats = await library.Catalog.GetStorageStatsAsync();
        Assert.AreEqual(4, otherStats.Sources);
        Assert.AreEqual(2, (await library.Catalog.QueryAsync(new MemoryQuery())).Count);
    }

    [TestMethod]
    public async Task Mp4RotationAndExifOrientation_AreReportedWithoutDoubleRotation()
    {
        var jpeg = MediaFactory.Jpeg(600, 400, new DateOnly(2021, 1, 1), orientation: 6);
        var jpegInfo = MediaInspector.Inspect(new MemoryStream(jpeg));
        Assert.AreEqual(600, jpegInfo.Width);
        Assert.AreEqual(400, jpegInfo.Height);
        Assert.AreEqual(90, jpegInfo.Rotation);
        Assert.AreEqual(new DateOnly(2021, 1, 1), jpegInfo.ExifDate);
        Assert.AreEqual("2021:01:01 12:34:56", jpegInfo.ExifDateRaw);

        var mp4 = MediaFactory.Mp4(
            new DateTimeOffset(2020, 2, 3, 4, 5, 6, TimeSpan.Zero),
            800,
            600,
            180,
            4200,
            false);
        var mp4Info = MediaInspector.Inspect(new MemoryStream(mp4));
        Assert.AreEqual("mp4", mp4Info.Format);
        Assert.AreEqual(180, mp4Info.Rotation);
        Assert.AreEqual(800, mp4Info.Width);
        Assert.AreEqual(600, mp4Info.Height);
        Assert.AreEqual(4200, mp4Info.DurationMs);
        Assert.IsFalse(mp4Info.HasAudio);
        Assert.AreEqual(
            new DateTimeOffset(2020, 2, 3, 4, 5, 6, TimeSpan.Zero),
            mp4Info.CreationTimeUtc);

        var heic = MediaInspector.Inspect(new MemoryStream(MediaFactory.Heic(4032, 3024, 1)));
        Assert.AreEqual("heic", heic.Format);
        Assert.AreEqual(4032, heic.Width);
        Assert.AreEqual(3024, heic.Height);
        Assert.AreEqual(90, heic.Rotation);

        var mov = MediaInspector.Inspect(new MemoryStream(MediaFactory.Mov(
            new DateTimeOffset(2022, 7, 8, 9, 10, 11, TimeSpan.Zero),
            1280,
            720,
            270,
            2000,
            false)));
        Assert.AreEqual("mov", mov.Format);
        Assert.AreEqual(1280, mov.Width);
        Assert.AreEqual(720, mov.Height);
        Assert.AreEqual(270, mov.Rotation);
        Assert.AreEqual(2000, mov.DurationMs);
        Assert.IsFalse(mov.HasAudio);
    }

    private static MemoryCard AssertSingle(IReadOnlyList<MemoryCard> cards)
    {
        Assert.AreEqual(1, cards.Count);
        return cards[0];
    }
}
