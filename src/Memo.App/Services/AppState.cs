using System.Collections.ObjectModel;
using System.Text.Json;
using Memo.Core;
using Memo.Media;

namespace Memo.App.Services;

/// <summary>
/// État applicatif partagé. Une bibliothèque réelle (Memo.Core) remplace le
/// jeu de démonstration dès qu'elle est ouverte ; les pages ne changent pas.
/// </summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    public MemoLibrary? Library { get; private set; }
    public bool IsDemo => Library is null;
    public string LibraryName { get; private set; } = "Bibliothèque de démonstration";
    public string? LibraryPath { get; private set; }
    public IReadOnlyList<SpaceInfo> Spaces { get; private set; } = Array.Empty<SpaceInfo>();
    public long CurrentSpaceId { get; private set; }

    public ObservableCollection<MemoryCard> Cards { get; } = new();
    public ObservableCollection<CollectionInfo> Albums { get; } = new();
    public ObservableCollection<CollectionInfo> Recaps { get; } = new();
    public AppSettings Settings { get; set; } = new(
        "Europe/Zurich", 4, true, true, false, false, "09:00", true,
        true, "System", false, false);

    public event EventHandler? LibraryChanged;

    private readonly Dictionary<long, string> _demoThumbnails = new();
    private readonly Dictionary<long, string> _demoMedia = new();
    private readonly Dictionary<long, string> _demoOverlays = new();
    // Récapitulatifs de démonstration conservés en mémoire, dans l'ordre enregistré.
    private readonly Dictionary<long, List<long>> _demoRecapItems = new();

    private AppState() { }

    public void LoadDemo()
    {
        Library = null;
        LibraryPath = null;
        LibraryName = "Bibliothèque de démonstration";
        Spaces = Array.Empty<SpaceInfo>();
        CurrentSpaceId = 0;
        Cards.Clear();
        Albums.Clear();
        Recaps.Clear();
        _demoThumbnails.Clear();
        _demoMedia.Clear();
        _demoOverlays.Clear();
        _demoRecapItems.Clear();

        foreach (var card in DemoData.CreateCards())
        {
            Cards.Add(card);
        }
        DemoData.FillAssets(_demoThumbnails, _demoMedia, _demoOverlays);
        foreach (var album in DemoData.CreateAlbums())
        {
            Albums.Add(album);
        }
        Settings = new AppSettings("Europe/Zurich", 4, true, true, false, false, "09:00", true, true, "System", false, false);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task OpenLibraryAsync(string rootPath, CancellationToken ct = default)
    {
        var library = await MemoLibrary.OpenAsync(rootPath, ct);
        Library = library;
        LibraryPath = library.Info.RootPath;
        LibraryName = library.Info.Name;
        Settings = library.Settings.Current;
        RememberLibrary(library.Info.RootPath);
        await ReloadAsync(ct);
    }

    public async Task CreateLibraryAsync(string rootPath, string name, CancellationToken ct = default)
    {
        var library = await MemoLibrary.CreateAsync(rootPath, name, Settings.TimeZoneId, ct);
        Library = library;
        LibraryPath = library.Info.RootPath;
        LibraryName = library.Info.Name;
        RememberLibrary(library.Info.RootPath);
        await ReloadAsync(ct);
    }

    public async Task CloseLibraryAsync()
    {
        if (Library is not null)
        {
            await Library.DisposeAsync();
        }
        LoadDemo();
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        if (Library is null)
        {
            return;
        }

        Spaces = await Library.Catalog.GetSpacesAsync(ct);
        if (Spaces.Count == 0)
        {
            var space = await Library.Catalog.CreateSpaceAsync("Mon export", null, ct);
            Spaces = new[] { space };
        }
        if (CurrentSpaceId == 0 || Spaces.All(s => s.Id != CurrentSpaceId))
        {
            CurrentSpaceId = Spaces[0].Id;
        }

        var cards = await Library.Catalog.QueryAsync(
            new MemoryQuery(IncludeExcluded: true, IncludeUndated: true, Limit: int.MaxValue), ct);
        Cards.Clear();
        foreach (var card in cards)
        {
            Cards.Add(card);
        }

        Albums.Clear();
        foreach (var album in await Library.Catalog.GetCollectionsAsync(CollectionKind.Album, ct))
        {
            Albums.Add(album);
        }

        Recaps.Clear();
        foreach (var recap in await Library.Catalog.GetCollectionsAsync(CollectionKind.Recap, ct))
        {
            Recaps.Add(recap);
        }

        Settings = Library.Settings.Current;
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ImportResult> ImportAsync(
        long spaceId,
        IReadOnlyList<string> paths,
        IProgress<ImportProgress>? progress,
        CancellationToken ct = default)
    {
        if (Library is null)
        {
            throw new InvalidOperationException("Aucune bibliothèque n'est ouverte.");
        }

        var result = await Library.Import.RunAsync(new ImportRequest(spaceId, paths), progress, ct);

        // Les médias sont visibles immédiatement ; les miniatures se préparent
        // ensuite sans bloquer la consultation.
        await ReloadAsync(ct);
        progress?.Report(new ImportProgress(
            ImportPhase.Analyzing,
            "Préparation des miniatures",
            result.Counters.MediaImported,
            result.Counters.MediaImported,
            null,
            result.Counters));
        await GenerateMissingThumbnailsAsync(progress, result.Counters, ct);
        await ReloadAsync(ct);
        return result;
    }

    public async Task GenerateMissingThumbnailsAsync(
        IProgress<ImportProgress>? progress = null,
        ImportCounters? counters = null,
        CancellationToken ct = default)
    {
        if (Library is null)
        {
            return;
        }

        var cards = await Library.Catalog.QueryAsync(
            new MemoryQuery(IncludeExcluded: true, IncludeUndated: true, Limit: int.MaxValue), ct);
        var missing = cards
            .Where(c => !c.HasThumbnail && c.MainAssetId is long)
            .ToList();
        var done = 0;
        var thumbsDirectory = Path.Combine(Library.Info.RootPath, "derived", "thumbs");
        Directory.CreateDirectory(thumbsDirectory);

        foreach (var card in missing)
        {
            ct.ThrowIfCancellationRequested();
            if (card.HasThumbnail || card.MainAssetId is not long assetId)
            {
                continue;
            }

            var source = Library.Catalog.GetAssetPath(assetId);
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(thumbsDirectory, $"{card.Id}.jpg");
            var kind = card.Kind;
            var ok = await Task.Run(() => kind == MediaKind.Video
                ? ThumbnailService.CreateVideoThumbnail(source, destination)
                : ThumbnailService.CreateImageThumbnail(source, destination), ct);
            if (ok)
            {
                await Library.Catalog.SetThumbnailAsync(card.Id, $"derived/thumbs/{card.Id}.jpg", ct);
            }

            done++;
            progress?.Report(new ImportProgress(
                ImportPhase.Analyzing,
                "Préparation des miniatures",
                done,
                missing.Count,
                null,
                counters ?? ImportCounters.Empty));
        }
    }

    public async Task SetFavoriteAsync(long memoryId, bool value, CancellationToken ct = default)
    {
        if (Library is null)
        {
            Replace(c => c.Id == memoryId, c => c with { IsFavorite = value });
            return;
        }
        await Library.Catalog.SetFavoriteAsync(new[] { memoryId }, value, ct);
        await ReloadAsync(ct);
    }

    public async Task SetExcludedAsync(long memoryId, bool value, CancellationToken ct = default)
    {
        if (Library is null)
        {
            Replace(c => c.Id == memoryId, c => c with { IsExcluded = value });
            return;
        }
        await Library.Catalog.SetExcludedAsync(new[] { memoryId }, value, ct);
        await ReloadAsync(ct);
    }

    public async Task SetDateOverrideAsync(long memoryId, DateOnly? date, CancellationToken ct = default)
    {
        if (Library is null)
        {
            Replace(c => c.Id == memoryId, c => c with { LocalDate = date, DateSource = date is null ? DateSource.Unknown : DateSource.User });
            return;
        }
        await Library.Catalog.SetDateOverrideAsync(memoryId, date, DatePrecision.Day, null, ct);
        await ReloadAsync(ct);
    }

    public async Task SetLocationAsync(long memoryId, double? lat, double? lon, string? label, CancellationToken ct = default)
    {
        if (Library is null)
        {
            Replace(c => c.Id == memoryId, c => c with { Latitude = lat, Longitude = lon, LocationLabel = label });
            return;
        }
        await Library.Catalog.SetLocationAsync(memoryId, lat, lon, label, ct);
        await ReloadAsync(ct);
    }

    public async Task CreateAlbumAsync(string name, CancellationToken ct = default)
    {
        if (Library is null)
        {
            var id = Albums.Count == 0 ? 1 : Albums.Max(a => a.Id) + 1;
            Albums.Add(new CollectionInfo(id, name, CollectionKind.Album, DateTimeOffset.Now, 0, null, null, null));
            return;
        }
        await Library.Catalog.CreateCollectionAsync(name, CollectionKind.Album, ct);
        await ReloadAsync(ct);
    }

    public async Task RenameAlbumAsync(long albumId, string name, CancellationToken ct = default)
    {
        if (Library is null)
        {
            for (var i = 0; i < Albums.Count; i++)
            {
                if (Albums[i].Id == albumId)
                {
                    Albums[i] = Albums[i] with { Name = name };
                }
            }
            return;
        }
        await Library.Catalog.RenameCollectionAsync(albumId, name, ct);
        await ReloadAsync(ct);
    }

    public async Task DeleteAlbumAsync(long albumId, CancellationToken ct = default)
    {
        if (Library is null)
        {
            var album = Albums.FirstOrDefault(a => a.Id == albumId);
            if (album is not null)
            {
                Albums.Remove(album);
            }
            return;
        }
        await Library.Catalog.DeleteCollectionAsync(albumId, ct);
        await ReloadAsync(ct);
    }

    public async Task AddToAlbumAsync(long albumId, IReadOnlyList<long> memoryIds, CancellationToken ct = default)
    {
        if (Library is null)
        {
            return;
        }
        await Library.Catalog.AddToCollectionAsync(albumId, memoryIds, ct);
        await ReloadAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryCard>> GetAlbumItemsAsync(long albumId, CancellationToken ct = default)
    {
        if (Library is null)
        {
            // Un récapitulatif de démonstration conserve son propre ordre ; les albums
            // historiques gardent l'ordre chronologique de la bibliothèque.
            if (_demoRecapItems.TryGetValue(albumId, out var recapIds))
            {
                return recapIds
                    .Select(id => Cards.FirstOrDefault(c => c.Id == id))
                    .OfType<MemoryCard>()
                    .ToList();
            }
            var ids = DemoData.AlbumMembers(albumId);
            return Cards.Where(c => ids.Contains(c.Id)).ToList();
        }
        return await Library.Catalog.GetCollectionItemsAsync(albumId, ct);
    }

    public async Task<IReadOnlyList<MemoryCard>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (Library is null)
        {
            var q = query.Trim();
            return Cards.Where(c =>
                (c.LocationLabel?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                c.ExternalKey.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (c.LocalDate?.ToString("d MMMM yyyy", French).Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(c => c.LocalDate)
                .ToList();
        }
        return await Library.Catalog.QueryAsync(
            new MemoryQuery(SearchText: query, IncludeExcluded: true, IncludeUndated: true, Limit: 500), ct);
    }

    public async Task<CollectionInfo?> SaveRecapAsync(string name, IReadOnlyList<long> memoryIds, CancellationToken ct = default)
    {
        if (Library is null)
        {
            var id = NextDemoRecapId();
            var info = BuildDemoRecap(id, name, memoryIds);
            _demoRecapItems[id] = memoryIds.Distinct().ToList();
            Recaps.Add(info);
            return info;
        }

        var collection = await Library.Catalog.CreateCollectionAsync(name, CollectionKind.Recap, ct);
        var ordered = memoryIds.Distinct().ToList();
        await Library.Catalog.AddToCollectionAsync(collection.Id, ordered, ct);
        await Library.Catalog.ReorderCollectionAsync(collection.Id, ordered, ct);
        await ReloadAsync(ct);
        return Recaps.FirstOrDefault(r => r.Id == collection.Id);
    }

    /// <summary>
    /// Met à jour un récapitulatif existant : nom, contenu et ordre, sans recréer la collection.
    /// </summary>
    public async Task<CollectionInfo?> UpdateRecapAsync(
        long recapId,
        string name,
        IReadOnlyList<long> memoryIds,
        CancellationToken ct = default)
    {
        if (Library is null)
        {
            var index = -1;
            for (var i = 0; i < Recaps.Count; i++)
            {
                if (Recaps[i].Id == recapId)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
            {
                return null;
            }

            var existing = Recaps[index];
            var info = BuildDemoRecap(recapId, name, memoryIds) with { CreatedAt = existing.CreatedAt };
            _demoRecapItems[recapId] = memoryIds.Distinct().ToList();
            Recaps[index] = info;
            return info;
        }

        await Library.Catalog.RenameCollectionAsync(recapId, name, ct);
        var existingItems = await Library.Catalog.GetCollectionItemsAsync(recapId, ct);
        var ordered = memoryIds.Distinct().ToList();
        var keep = ordered.ToHashSet();
        var removals = existingItems.Where(c => !keep.Contains(c.Id)).Select(c => c.Id).ToList();
        if (removals.Count > 0)
        {
            await Library.Catalog.RemoveFromCollectionAsync(recapId, removals, ct);
        }
        await Library.Catalog.AddToCollectionAsync(recapId, ordered, ct);
        await Library.Catalog.ReorderCollectionAsync(recapId, ordered, ct);
        await ReloadAsync(ct);
        return Recaps.FirstOrDefault(r => r.Id == recapId);
    }

    private long NextDemoRecapId()
    {
        // Les albums de démonstration occupent les petits identifiants ; les
        // récapitulatifs utilisent une plage distincte pour éviter toute collision.
        var maxRecap = Recaps.Count == 0 ? 0 : Recaps.Max(r => r.Id);
        return Math.Max(1000, maxRecap + 1);
    }

    private CollectionInfo BuildDemoRecap(long id, string name, IReadOnlyList<long> memoryIds)
    {
        var cards = memoryIds
            .Distinct()
            .Select(memoryId => Cards.FirstOrDefault(c => c.Id == memoryId))
            .OfType<MemoryCard>()
            .ToList();
        var dates = cards.Where(c => c.LocalDate is not null).Select(c => c.LocalDate!.Value).ToList();
        return new CollectionInfo(
            id,
            name,
            CollectionKind.Recap,
            DateTimeOffset.Now,
            cards.Count,
            cards.Count > 0 ? ThumbnailFor(cards[0].Id) : null,
            dates.Count > 0 ? dates.Min() : null,
            dates.Count > 0 ? dates.Max() : null);
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        if (Library is not null)
        {
            await Library.Settings.SaveAsync(settings, ct);
        }
    }

    public async Task SetTimeZoneAsync(string timeZoneId, CancellationToken ct = default)
    {
        Settings = Settings with { TimeZoneId = timeZoneId };
        if (Library is not null)
        {
            await Library.Settings.SaveAsync(Settings, ct);
            await Library.Catalog.RecalculateClassificationAsync(timeZoneId, ct);
            await ReloadAsync(ct);
        }
    }

    public async Task<string> BackupAsync(string destinationZip, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (Library is null)
        {
            throw new InvalidOperationException("Ouvrez une bibliothèque avant de la sauvegarder.");
        }
        var result = await Library.Backup.CreateBackupAsync(destinationZip, progress, ct);
        return result.Path;
    }

    public async Task RestoreAsync(string zipPath, string destinationRoot, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (Library is null)
        {
            throw new InvalidOperationException("Ouvrez une bibliothèque avant de restaurer une sauvegarde.");
        }
        await Library.Backup.RestoreAsync(zipPath, destinationRoot, progress, ct);
    }

    public string? ThumbnailFor(long memoryId)
    {
        if (Library is not null)
        {
            var path = Library.Catalog.GetThumbnailPath(memoryId);
            if (path is not null)
            {
                return path;
            }
            var card = Cards.FirstOrDefault(c => c.Id == memoryId);
            if (card?.Kind == MediaKind.Photo && card.MainAssetId is long assetId)
            {
                return Library.Catalog.GetAssetPath(assetId);
            }
            return null;
        }
        return _demoThumbnails.GetValueOrDefault(memoryId);
    }

    public string? MediaFor(long memoryId)
    {
        if (Library is not null)
        {
            var card = Cards.FirstOrDefault(c => c.Id == memoryId);
            return card?.MainAssetId is long id ? Library.Catalog.GetAssetPath(id) : null;
        }
        return _demoMedia.GetValueOrDefault(memoryId);
    }

    public string? OverlayFor(long memoryId)
    {
        if (Library is not null)
        {
            var card = Cards.FirstOrDefault(c => c.Id == memoryId);
            return card?.OverlayAssetId is long id ? Library.Catalog.GetAssetPath(id) : null;
        }
        return _demoOverlays.GetValueOrDefault(memoryId);
    }

    public IReadOnlyList<MemoryCard> ThisDay(DateOnly day)
        => Cards
            .Where(c => !c.IsExcluded && c.DatePrecision >= DatePrecision.Day && c.LocalDate is { } d && d.Month == day.Month && d.Day == day.Day && d.Year < day.Year)
            .OrderByDescending(c => c.LocalDate)
            .ToList();

    public IReadOnlyList<MemoryCard> OneYearAgo(DateOnly day)
        => Cards
            .Where(c => !c.IsExcluded && c.DatePrecision >= DatePrecision.Day && c.LocalDate is { } d && d.Year == day.Year - 1 && d.Month == day.Month && d.Day == day.Day)
            .ToList();

    public IReadOnlyList<MemoryCard> Undated()
        => Cards.Where(c => c.LocalDate is null).ToList();

    public DateOnly Today
    {
        get
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(Settings.TimeZoneId);
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
            }
            catch
            {
                return DateOnly.FromDateTime(DateTime.Now);
            }
        }
    }

    public string FormatDate(MemoryCard card)
        => card.LocalDate is { } d ? d.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy", French) : "Date inconnue";

    public static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    public static string LastLibraryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Memo", "app.json");

    public void RememberLibrary(string path)
    {
        try
        {
            var file = LastLibraryPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(new { lastLibrary = path }));
        }
        catch
        {
            // mémorisation best-effort
        }
    }

    public static string? ReadLastLibrary()
    {
        try
        {
            var file = LastLibraryPath;
            if (!File.Exists(file))
            {
                return null;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty("lastLibrary", out var property) ? property.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private void Replace(Func<MemoryCard, bool> predicate, Func<MemoryCard, MemoryCard> update)
    {
        for (var i = 0; i < Cards.Count; i++)
        {
            if (predicate(Cards[i]))
            {
                Cards[i] = update(Cards[i]);
            }
        }
    }
}








