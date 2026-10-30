namespace Memo.Core.Tests;

[TestClass]
public sealed class SettingsAndAnalysisTests : TestBase
{
    [TestMethod]
    public async Task Settings_ArePersisted_AndValidated()
    {
        var root = NewDirectory();
        var changes = 0;
        await using (var library = await MemoLibrary.CreateAsync(root, "Réglages", "Europe/Paris"))
        {
            library.Settings.Changed += (_, _) => changes++;
            var updated = library.Settings.Current with
            {
                PhotoDurationSeconds = 9,
                Theme = "dark",
                OcrEnabled = true,
                ReminderTime = "08:15",
            };
            await library.Settings.SaveAsync(updated);
            Assert.AreEqual(1, changes);
            Assert.AreEqual(9, library.Settings.Current.PhotoDurationSeconds);

            await ThrowsAsync<ArgumentException>(() =>
                library.Settings.SaveAsync(updated with { TimeZoneId = "Nowhere/Invalid" }));
            await ThrowsAsync<ArgumentOutOfRangeException>(() =>
                library.Settings.SaveAsync(updated with { PhotoDurationSeconds = 0 }));
            await ThrowsAsync<ArgumentException>(() =>
                library.Settings.SaveAsync(updated with { ReminderTime = "25:99" }));
        }

        await using var reopened = await MemoLibrary.OpenAsync(root);
        Assert.AreEqual(9, reopened.Settings.Current.PhotoDurationSeconds);
        Assert.AreEqual("dark", reopened.Settings.Current.Theme);
        Assert.IsTrue(reopened.Settings.Current.OcrEnabled);
        Assert.AreEqual("08:15", reopened.Settings.Current.ReminderTime);
        Assert.AreEqual("Europe/Paris", reopened.Settings.Current.TimeZoneId);
    }

    [TestMethod]
    public async Task OcrAndVisualHashes_AreStoredSearchedAndCleared()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "analysis.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(120, 90, new DateOnly(2020, 1, 1))));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var card = (await library.Catalog.QueryAsync(new MemoryQuery()))[0];

        await library.Analysis.SetOcrTextAsync(card.Id, "texte reconnaissable unique", "synthetic");
        Assert.AreEqual("texte reconnaissable unique", await library.Analysis.GetOcrTextAsync(card.Id));
        Assert.AreEqual(1, (await library.Catalog.QueryAsync(
            new MemoryQuery { SearchText = "reconnaissable" })).Count);

        var hash = ulong.MaxValue - 3;
        await library.Analysis.SetVisualHashAsync(card.Id, hash);
        var hashes = await library.Analysis.GetVisualHashesAsync();
        Assert.AreEqual(1, hashes.Count);
        Assert.AreEqual(card.Id, hashes[0].MemoryId);
        Assert.AreEqual(hash, hashes[0].Hash);

        await library.Analysis.ClearOcrAsync();
        Assert.IsNull(await library.Analysis.GetOcrTextAsync(card.Id));
        Assert.AreEqual(0, (await library.Catalog.QueryAsync(
            new MemoryQuery { SearchText = "reconnaissable" })).Count);

        await library.Analysis.ClearVisualAsync();
        Assert.AreEqual(0, (await library.Analysis.GetVisualHashesAsync()).Count);
    }

    [TestMethod]
    public async Task PersonGroups_MergeAssignAndClearFaces()
    {
        var uuid = Guid.NewGuid();
        var zip = await ZipFactory.CreateAsync(
            Path.Combine(Root, "faces.zip"), null,
            ($"memories/{uuid}-main.jpg", MediaFactory.Jpeg(200, 200, new DateOnly(2019, 3, 4))));

        await using var library = await CreateLibraryAsync();
        var space = await NewSpaceAsync(library);
        await library.Import.RunAsync(new ImportRequest(space.Id, new[] { zip }));
        var card = (await library.Catalog.QueryAsync(new MemoryQuery()))[0];

        var analysis = (AnalysisStore)library.Analysis;
        var groupA = await analysis.CreatePersonGroupAsync("Personne A");
        var groupB = await analysis.CreatePersonGroupAsync(null);
        var faceA = await analysis.AddFaceAsync(card.Id, 10, 20, 30, 40, groupA, new byte[] { 1, 2, 3 });
        var faceB = await analysis.AddFaceAsync(card.Id, 50, 60, 20, 20, groupB);

        var groups = await analysis.GetPersonGroupsAsync();
        Assert.AreEqual(2, groups.Count);
        Assert.AreEqual(1, groups.Single(group => group.Id == groupA).FaceCount);
        Assert.AreEqual(1, groups.Single(group => group.Id == groupB).FaceCount);
        CollectionAssert.AreEquivalent(
            new[] { card.Id },
            (await analysis.GetMemoriesForPersonAsync(groupA)).ToArray());
        Assert.AreEqual(1, (await library.Catalog.QueryAsync(
            new MemoryQuery { PersonGroupId = groupA })).Count);
        Assert.IsNotNull(groups.Single(group => group.Id == groupA).CoverPath);

        await analysis.AssignFaceAsync(faceA, groupB);
        Assert.AreEqual(2, (await analysis.GetPersonGroupsAsync()).Single(group => group.Id == groupB).FaceCount);

        await analysis.MergePersonGroupsAsync(groupB, groupA);
        var merged = await analysis.GetPersonGroupsAsync();
        Assert.AreEqual(1, merged.Count);
        Assert.AreEqual(groupB, merged[0].Id);
        Assert.AreEqual(2, merged[0].FaceCount);

        await analysis.RenamePersonGroupAsync(groupB, "Personne fusionnée");
        Assert.AreEqual("Personne fusionnée", (await analysis.GetPersonGroupsAsync())[0].Name);

        await analysis.AssignFaceAsync(faceB, null);
        Assert.AreEqual(1, (await analysis.GetPersonGroupsAsync())[0].FaceCount);

        await analysis.ClearFacesAsync();
        var remainingGroup = (await analysis.GetPersonGroupsAsync()).Single();
        Assert.AreEqual(0, remainingGroup.FaceCount);
        Assert.AreEqual(0, (await analysis.GetFacesAsync()).Count);
        await analysis.DeletePersonGroupAsync(remainingGroup.Id);
        Assert.AreEqual(0, (await analysis.GetPersonGroupsAsync()).Count);
    }
}
