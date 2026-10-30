using Memo.Core;
using Memo.TestSupport;

namespace Memo.Integration.Tests;

[TestClass]
public sealed class RobustnessTests : IntegrationTestBase
{
    [TestMethod]
    public async Task TruncatedZip_ProducesDiagnostic_WithoutThrowing()
    {
        var memory = SyntheticMedia.Photo(60, "ARCHIVE TRONQUEE", seed: 60);
        var path = ArchiveBuilder.WriteComplete(
            Path.Combine(Root, "tronquee.zip"),
            new[] { memory });
        ExportBuilder.CorruptArchive(path, 0.55);

        await using var library = await CreateLibraryAsync();
        var space = await CreateSpaceAsync(library);
        var result = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { path }));

        Assert.IsFalse(result.Cancelled);
        Assert.IsTrue(result.Counters.Errors > 0);
        Assert.IsTrue(
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == "error" &&
                diagnostic.Code == "archive.corrupt"),
            string.Join(Environment.NewLine, result.Diagnostics.Select(
                diagnostic => $"{diagnostic.Severity}:{diagnostic.Code}:{diagnostic.Message}")));
        Assert.AreEqual(0, (await library.Catalog.QueryAsync(new MemoryQuery())).Count);
        var batches = await library.Catalog.GetBatchesAsync();
        Assert.AreEqual("failed", batches[0].Status);
    }

    [TestMethod]
    public async Task EntryWithParentTraversal_IsRejected_AndGoodEntryImported()
    {
        var good = SyntheticMedia.Photo(61, "ENTREE VALIDE", seed: 61);
        var path = ExportBuilder.Write(
            Path.Combine(Root, "traversee.zip"),
            new[] { good },
            new ExportArchiveOptions
            {
                Roles = SyntheticRoleFilter.Both,
                IncludeIndexHtml = true,
                IncludeJson = true,
                IncludeTraversalEntry = true,
                TraversalEntryName = "../hors-export.jpg",
            });

        await using var library = await CreateLibraryAsync();
        var space = await CreateSpaceAsync(library);
        var result = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { path }));

        Assert.IsFalse(result.Cancelled);
        Assert.IsTrue(
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "entry.invalid-path" &&
                diagnostic.EntryPath is not null &&
                diagnostic.EntryPath.Contains("..", StringComparison.Ordinal)));
        var cards = await library.Catalog.QueryAsync(new MemoryQuery());
        Assert.AreEqual(1, cards.Count);
        Assert.AreEqual(good.Id.ToString("D"), cards[0].ExternalKey);
        Assert.AreEqual(
            0,
            Directory
                .EnumerateFiles(Root, "hors-export.jpg", SearchOption.AllDirectories)
                .Count());
    }

    [TestMethod]
    public async Task EmptyZip_CompletesWithoutCreatingMemories()
    {
        var path = ExportBuilder.Write(
            Path.Combine(Root, "vide.zip"),
            Array.Empty<SyntheticMemory>(),
            new ExportArchiveOptions
            {
                Roles = SyntheticRoleFilter.None,
                IncludeIndexHtml = false,
                IncludeJson = false,
            });
        Assert.AreEqual(0, ExportBuilder.ListEntries(path).Count);

        await using var library = await CreateLibraryAsync();
        var space = await CreateSpaceAsync(library);
        var result = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { path }));

        Assert.IsFalse(result.Cancelled);
        Assert.AreEqual(0, result.Counters.Errors);
        Assert.AreEqual(1, result.Counters.ArchivesSeen);
        Assert.AreEqual(1, result.Counters.ArchivesImported);
        Assert.AreEqual(0, result.Counters.EntriesSeen);
        Assert.AreEqual(0, result.Counters.MemoriesCreated);
        Assert.AreEqual(0, result.Counters.MediaImported);
        Assert.AreEqual(0, (await library.Catalog.QueryAsync(new MemoryQuery())).Count);
        var batches = await library.Catalog.GetBatchesAsync();
        Assert.AreEqual("completed", batches[0].Status);
    }

    [TestMethod]
    public async Task MissingPath_IsReportedAsFatalWithoutCrash()
    {
        var missing = Path.Combine(Root, "chemin-inexistant", "archive.zip");

        await using var library = await CreateLibraryAsync();
        var space = await CreateSpaceAsync(library);
        var result = await library.Import.RunAsync(new ImportRequest(space.Id, new[] { missing }));

        Assert.IsFalse(result.Cancelled);
        Assert.AreEqual(0, result.Counters.ArchivesSeen);
        Assert.AreEqual(0, result.Counters.ArchivesImported);
        Assert.IsTrue(result.Counters.Errors > 0);
        Assert.IsTrue(
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Severity == "error" &&
                diagnostic.Code == "path.missing"));
        Assert.AreEqual(0, (await library.Catalog.QueryAsync(new MemoryQuery())).Count);
        var batches = await library.Catalog.GetBatchesAsync();
        Assert.AreEqual("failed", batches[0].Status);
    }

    [TestMethod]
    public async Task Restore_RefusesNonEmptyDestination_AndPreservesItsContent()
    {
        var memory = SyntheticMedia.Photo(62, "RESTAURATION", seed: 62);
        var archive = ArchiveBuilder.WriteComplete(
            Path.Combine(Root, "restauration-source.zip"),
            new[] { memory });

        await using var library = await CreateLibraryAsync();
        var space = await CreateSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { archive }));
        var backupPath = Path.Combine(Root, "restauration.zip");
        await library.Backup.CreateBackupAsync(backupPath);

        var destination = Path.Combine(Root, "dossier-occupe");
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "important.txt");
        await File.WriteAllTextAsync(sentinel, "à conserver");

        var exception = await ThrowsAsync<InvalidOperationException>(() =>
            library.Backup.RestoreAsync(backupPath, destination));
        StringAssert.Contains(exception.Message, "n'est pas vide");
        Assert.AreEqual("à conserver", await File.ReadAllTextAsync(sentinel));
        CollectionAssert.AreEqual(
            new[] { "important.txt" },
            Directory.GetFileSystemEntries(destination)
                .Select(Path.GetFileName)
                .ToArray());
        Assert.AreEqual(
            0,
            Directory.GetDirectories(Root, ".dossier-occupe.restore-*").Length);
    }
}
