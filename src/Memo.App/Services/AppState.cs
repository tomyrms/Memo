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

    // Index id -> position dans Cards, entretenu avec la collection pour que
    // les mises à jour incrémentales restent en O(1) par souvenir.
    private readonly Dictionary<long, int> _cardIndex = new();

    // Sérialise les changements de bibliothèque (ouverture, création, fermeture)
    // afin que deux bascules concurrentes ne puissent pas s'entrelacer.
    private readonly SemaphoreSlim _libraryGate = new(1, 1);

    // Jeton de génération : toute opération asynchrone capture la bibliothèque
    // et cette valeur, puis vérifie qu'elles sont toujours courantes avant
    // d'écrire dans l'état. Une opération lancée sur l'ancienne bibliothèque
    // ne peut donc pas polluer la nouvelle.
    private int _libraryEpoch;
    private int _reloadVersion;

    private AppState() { }

    public void LoadDemo()
    {
        // Invalide les opérations encore en vol sur une éventuelle bibliothèque.
        _libraryEpoch++;
        Library = null;
        LibraryPath = null;
        LibraryName = "Bibliothèque de démonstration";
        Spaces = Array.Empty<SpaceInfo>();
        CurrentSpaceId = 0;
        _demoThumbnails.Clear();
        _demoMedia.Clear();
        _demoOverlays.Clear();
        _demoRecapItems.Clear();

        ReplaceAllCards(DemoData.CreateCards());
        Reset(Albums, Array.Empty<CollectionInfo>());
        Reset(Recaps, Array.Empty<CollectionInfo>());
        DemoData.FillAssets(_demoThumbnails, _demoMedia, _demoOverlays);
        foreach (var album in DemoData.CreateAlbums())
        {
            Albums.Add(album);
        }
        Settings = new AppSettings("Europe/Zurich", 4, true, true, false, false, "09:00", true, true, "System", false, false);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task OpenLibraryAsync(string rootPath, CancellationToken ct = default)
        => SwitchLibraryAsync(token => MemoLibrary.OpenAsync(rootPath, token), ct);

    public Task CreateLibraryAsync(string rootPath, string name, CancellationToken ct = default)
    {
        // Le fuseau courant sert de valeur initiale au nouveau catalogue.
        var timeZoneId = Settings.TimeZoneId;
        return SwitchLibraryAsync(token => MemoLibrary.CreateAsync(rootPath, name, timeZoneId, token), ct);
    }

    public async Task CloseLibraryAsync()
    {
        await _libraryGate.WaitAsync();
        try
        {
            var previous = Library;
            _libraryEpoch++;
            Library = null;
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }
            LoadDemo();
        }
        finally
        {
            _libraryGate.Release();
        }
    }

    /// <summary>
    /// Bascule atomique : la nouvelle bibliothèque est ouverte et son contenu
    /// est chargé avant de remplacer l'ancienne. En cas d'échec, l'état courant
    /// (et le chemin mémorisé) reste strictement inchangé.
    /// </summary>
    private async Task SwitchLibraryAsync(Func<CancellationToken, Task<MemoLibrary>> open, CancellationToken ct)
    {
        await _libraryGate.WaitAsync(ct);
        MemoLibrary? candidate = null;
        try
        {
            candidate = await open(ct);

            // Charge tout avant la bascule : Cards/Albums/Recaps ne sont jamais
            // vidés pour une bibliothèque qui ne s'ouvre pas entièrement.
            var snapshot = await LoadSnapshotAsync(candidate, 0, ct);

            var previous = Library;
            ApplyLibrary(candidate, snapshot);
            candidate = null; // l'instance appartient désormais à AppState

            // Dernière bibliothèque mémorisée uniquement après succès complet.
            RememberLibrary(LibraryPath!);

            if (previous is not null)
            {
                await previous.DisposeAsync();
            }
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (candidate is not null)
            {
                await candidate.DisposeAsync();
            }
            _libraryGate.Release();
        }
    }

    /// <summary>
    /// Mémorise l'espace source actif. Le choix sert de valeur par défaut aux
    /// imports suivants et reste préféré lors d'un rechargement de la bibliothèque.
    /// </summary>
    public void SelectSpace(long spaceId)
    {
        if (spaceId > 0 && Spaces.Any(space => space.Id == spaceId))
        {
            CurrentSpaceId = spaceId;
        }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
        {
            return;
        }

        var epoch = _libraryEpoch;
        // Un rechargement plus récent remplace celui-ci ; le résultat obsolète
        // n'est jamais appliqué.
        var version = ++_reloadVersion;
        var snapshot = await LoadSnapshotAsync(library, CurrentSpaceId, ct);
        if (!IsCurrent(library, epoch) || version != _reloadVersion)
        {
            return;
        }

        ApplySnapshot(snapshot);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ImportResult> ImportAsync(
        long spaceId,
        IReadOnlyList<string> paths,
        IProgress<ImportProgress>? progress,
        CancellationToken ct = default)
    {
        var library = Library ?? throw new InvalidOperationException("Aucune bibliothèque n'est ouverte.");
        var epoch = _libraryEpoch;

        var result = await library.Import.RunAsync(new ImportRequest(spaceId, paths), progress, ct);
        if (!IsCurrent(library, epoch))
        {
            // L'import s'est terminé après un changement de bibliothèque :
            // rien n'est appliqué à la nouvelle.
            return result;
        }

        // Les médias sont visibles immédiatement ; les miniatures se préparent
        // ensuite sans bloquer la consultation.
        await ReloadAsync(ct);
        if (!IsCurrent(library, epoch))
        {
            return result;
        }

        progress?.Report(new ImportProgress(
            ImportPhase.Analyzing,
            "Préparation des miniatures",
            result.Counters.MediaImported,
            result.Counters.MediaImported,
            null,
            result.Counters));
        await GenerateMissingThumbnailsAsync(progress, result.Counters, ct);
        return result;
    }

    public async Task GenerateMissingThumbnailsAsync(
        IProgress<ImportProgress>? progress = null,
        ImportCounters? counters = null,
        CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
        {
            return;
        }

        var epoch = _libraryEpoch;
        var cards = await library.Catalog.QueryAsync(
            new MemoryQuery(IncludeExcluded: true, IncludeUndated: true, Limit: int.MaxValue), ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }

        var missing = cards
            .Where(c => !c.HasThumbnail && c.MainAssetId is long)
            .ToList();
        var done = 0;
        var thumbsDirectory = Path.Combine(library.Info.RootPath, "derived", "thumbs");
        Directory.CreateDirectory(thumbsDirectory);

        foreach (var card in missing)
        {
            ct.ThrowIfCancellationRequested();
            // Une bascule de bibliothèque interrompt le travail restant : les
            // miniatures de l'ancienne bibliothèque n'ont plus à être produites.
            if (!IsCurrent(library, epoch))
            {
                return;
            }
            if (card.HasThumbnail || card.MainAssetId is not long assetId)
            {
                continue;
            }

            var source = library.Catalog.GetAssetPath(assetId);
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(thumbsDirectory, $"{card.Id}.jpg");
            var kind = card.Kind;
            var ok = await Task.Run(() => kind == MediaKind.Video
                ? ThumbnailService.CreateVideoThumbnail(source, destination)
                : ThumbnailService.CreateImageThumbnail(source, destination), ct);
            if (!IsCurrent(library, epoch))
            {
                return;
            }

            if (ok)
            {
                await library.Catalog.SetThumbnailAsync(card.Id, $"derived/thumbs/{card.Id}.jpg", ct);
                if (!IsCurrent(library, epoch))
                {
                    return;
                }

                // La carte est mise à jour sur place plutôt que de recharger
                // toute la collection après la génération des aperçus.
                ReplaceCard(card.Id, current => current with { HasThumbnail = true });
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

    /// <summary>Applique un favori à plusieurs souvenirs en une seule écriture.</summary>
    public async Task SetFavoritesAsync(IReadOnlyList<long> memoryIds, bool value, CancellationToken ct = default)
    {
        if (memoryIds.Count == 0)
        {
            return;
        }

        var ids = memoryIds.Distinct().ToHashSet();
        var library = Library;
        if (library is null)
        {
            Replace(c => ids.Contains(c.Id), c => c with { IsFavorite = value });
            return;
        }

        var epoch = _libraryEpoch;
        await library.Catalog.SetFavoriteAsync(ids.ToList(), value, ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }
        Replace(c => ids.Contains(c.Id), c => c with { IsFavorite = value });
    }

    public Task SetFavoriteAsync(long memoryId, bool value, CancellationToken ct = default)
        => SetFavoritesAsync(new[] { memoryId }, value, ct);

    /// <summary>Applique une exclusion à plusieurs souvenirs en une seule écriture.</summary>
    public async Task SetExclusionsAsync(IReadOnlyList<long> memoryIds, bool value, CancellationToken ct = default)
    {
        if (memoryIds.Count == 0)
        {
            return;
        }

        var ids = memoryIds.Distinct().ToHashSet();
        var library = Library;
        if (library is null)
        {
            Replace(c => ids.Contains(c.Id), c => c with { IsExcluded = value });
            return;
        }

        var epoch = _libraryEpoch;
        await library.Catalog.SetExcludedAsync(ids.ToList(), value, ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }
        Replace(c => ids.Contains(c.Id), c => c with { IsExcluded = value });
    }

    public Task SetExcludedAsync(long memoryId, bool value, CancellationToken ct = default)
        => SetExclusionsAsync(new[] { memoryId }, value, ct);

    public async Task SetDateOverrideAsync(long memoryId, DateOnly? date, CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
        {
            Replace(c => c.Id == memoryId, c => c with { LocalDate = date, DateSource = date is null ? DateSource.Unknown : DateSource.User });
            return;
        }

        var epoch = _libraryEpoch;
        await library.Catalog.SetDateOverrideAsync(memoryId, date, DatePrecision.Day, null, ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }

        // Effacer une correction restaure la date déduite des métadonnées :
        // relire cette seule carte évite de recharger toute la collection.
        await RefreshCardAsync(
            library,
            epoch,
            memoryId,
            current => current with { LocalDate = date, DateSource = date is null ? DateSource.Unknown : DateSource.User },
            ct);

        // Les dates de couverture d'un album peuvent avoir changé.
        if (IsCurrent(library, epoch))
        {
            await RefreshCollectionsAsync(library, epoch, ct);
        }
    }

    public async Task SetLocationAsync(long memoryId, double? lat, double? lon, string? label, CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
        {
            Replace(c => c.Id == memoryId, c => c with { Latitude = lat, Longitude = lon, LocationLabel = label });
            return;
        }

        var epoch = _libraryEpoch;
        await library.Catalog.SetLocationAsync(memoryId, lat, lon, label, ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }

        await RefreshCardAsync(
            library,
            epoch,
            memoryId,
            current => current with { Latitude = lat, Longitude = lon, LocationLabel = label },
            ct);
    }

    public async Task CreateAlbumAsync(string name, CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
        {
            var id = Albums.Count == 0 ? 1 : Albums.Max(a => a.Id) + 1;
            Albums.Add(new CollectionInfo(id, name, CollectionKind.Album, DateTimeOffset.Now, 0, null, null, null));
            return;
        }

        var epoch = _libraryEpoch;
        var album = await library.Catalog.CreateCollectionAsync(name, CollectionKind.Album, ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }
        InsertByName(Albums, album);
    }

    public async Task RenameAlbumAsync(long albumId, string name, CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
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

        var epoch = _libraryEpoch;
        await library.Catalog.RenameCollectionAsync(albumId, name, ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }

        var album = Albums.FirstOrDefault(a => a.Id == albumId);
        if (album is not null)
        {
            Albums.Remove(album);
            InsertByName(Albums, album with { Name = name });
        }
    }

    public async Task DeleteAlbumAsync(long albumId, CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
        {
            var album = Albums.FirstOrDefault(a => a.Id == albumId);
            if (album is not null)
            {
                Albums.Remove(album);
            }
            return;
        }

        var epoch = _libraryEpoch;
        await library.Catalog.DeleteCollectionAsync(albumId, ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }

        var removed = Albums.FirstOrDefault(a => a.Id == albumId);
        if (removed is not null)
        {
            Albums.Remove(removed);
        }
    }

    public async Task AddToAlbumAsync(long albumId, IReadOnlyList<long> memoryIds, CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
        {
            return;
        }

        var epoch = _libraryEpoch;
        await library.Catalog.AddToCollectionAsync(albumId, memoryIds, ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }

        // Seuls les compteurs et les dates de couverture changent : rafraîchir
        // les collections suffit, sans recharger les souvenirs.
        await RefreshCollectionsAsync(library, epoch, ct);
    }

    public async Task<IReadOnlyList<MemoryCard>> GetAlbumItemsAsync(long albumId, CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
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
        return await library.Catalog.GetCollectionItemsAsync(albumId, ct);
    }

    public async Task<IReadOnlyList<MemoryCard>> SearchAsync(string query, CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
        {
            var q = query.Trim();
            return Cards.Where(c =>
                (c.LocationLabel?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                c.ExternalKey.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (c.LocalDate?.ToString("d MMMM yyyy", French).Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(c => c.LocalDate)
                .ToList();
        }
        return await library.Catalog.QueryAsync(
            new MemoryQuery(SearchText: query, IncludeExcluded: true, IncludeUndated: true, Limit: 500), ct);
    }

    public async Task<CollectionInfo?> SaveRecapAsync(string name, IReadOnlyList<long> memoryIds, CancellationToken ct = default)
    {
        var library = Library;
        if (library is null)
        {
            var id = NextDemoRecapId();
            var info = BuildDemoRecap(id, name, memoryIds);
            _demoRecapItems[id] = memoryIds.Distinct().ToList();
            Recaps.Add(info);
            return info;
        }

        var epoch = _libraryEpoch;
        var ordered = memoryIds.Distinct().ToList();
        var collection = await library.Catalog.CreateCollectionAsync(name, CollectionKind.Recap, ct);
        await library.Catalog.AddToCollectionAsync(collection.Id, ordered, ct);
        await library.Catalog.ReorderCollectionAsync(collection.Id, ordered, ct);
        if (!IsCurrent(library, epoch))
        {
            return null;
        }

        await RefreshCollectionsAsync(library, epoch, ct);
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
        var library = Library;
        if (library is null)
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

        var epoch = _libraryEpoch;
        await library.Catalog.RenameCollectionAsync(recapId, name, ct);
        var existingItems = await library.Catalog.GetCollectionItemsAsync(recapId, ct);
        var ordered = memoryIds.Distinct().ToList();
        var keep = ordered.ToHashSet();
        var removals = existingItems.Where(c => !keep.Contains(c.Id)).Select(c => c.Id).ToList();
        if (removals.Count > 0)
        {
            await library.Catalog.RemoveFromCollectionAsync(recapId, removals, ct);
        }
        await library.Catalog.AddToCollectionAsync(recapId, ordered, ct);
        await library.Catalog.ReorderCollectionAsync(recapId, ordered, ct);
        if (!IsCurrent(library, epoch))
        {
            return null;
        }

        await RefreshCollectionsAsync(library, epoch, ct);
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
        var library = Library;
        if (library is not null)
        {
            await library.Settings.SaveAsync(settings, ct);
        }
    }

    public async Task SetTimeZoneAsync(string timeZoneId, CancellationToken ct = default)
    {
        Settings = Settings with { TimeZoneId = timeZoneId };
        var library = Library;
        if (library is null)
        {
            return;
        }

        var epoch = _libraryEpoch;
        await library.Settings.SaveAsync(Settings, ct);
        await library.Catalog.RecalculateClassificationAsync(timeZoneId, ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }

        // Recalcul structurel des dates classées : rechargement complet assumé.
        await ReloadAsync(ct);
    }

    public async Task<string> BackupAsync(string destinationZip, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var library = Library
            ?? throw new InvalidOperationException("Ouvrez une bibliothèque avant de la sauvegarder.");
        var result = await library.Backup.CreateBackupAsync(destinationZip, progress, ct);
        return result.Path;
    }

    /// <summary>
    /// Restaure une sauvegarde dans un dossier neuf. Ne dépend pas de la
    /// bibliothèque ouverte : le premier lancement peut donc restaurer.
    /// </summary>
    public Task RestoreAsync(string zipPath, string destinationRoot, IProgress<string>? progress = null, CancellationToken ct = default)
        => BackupRestoreService.RestoreAsync(zipPath, destinationRoot, progress, ct);

    public string? ThumbnailFor(long memoryId)
    {
        var library = Library;
        if (library is not null)
        {
            var path = library.Catalog.GetThumbnailPath(memoryId);
            if (path is not null)
            {
                return path;
            }
            var card = CardById(memoryId);
            if (card?.Kind == MediaKind.Photo && card.MainAssetId is long assetId)
            {
                return library.Catalog.GetAssetPath(assetId);
            }
            return null;
        }
        return _demoThumbnails.GetValueOrDefault(memoryId);
    }

    public string? MediaFor(long memoryId)
    {
        var library = Library;
        if (library is not null)
        {
            var card = CardById(memoryId);
            return card?.MainAssetId is long id ? library.Catalog.GetAssetPath(id) : null;
        }
        return _demoMedia.GetValueOrDefault(memoryId);
    }

    public string? OverlayFor(long memoryId)
    {
        var library = Library;
        if (library is not null)
        {
            var card = CardById(memoryId);
            return card?.OverlayAssetId is long id ? library.Catalog.GetAssetPath(id) : null;
        }
        return _demoOverlays.GetValueOrDefault(memoryId);
    }

    /// <summary>Retrouve la carte courante à partir de son identifiant.</summary>
    public MemoryCard? CardById(long memoryId)
    {
        if (_cardIndex.TryGetValue(memoryId, out var index) &&
            index >= 0 && index < Cards.Count && Cards[index].Id == memoryId)
        {
            return Cards[index];
        }

        // Repli si l'index est désynchronisé (collection modifiée ailleurs).
        return Cards.FirstOrDefault(c => c.Id == memoryId);
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

    /// <summary>Instantané complet d'une bibliothèque, chargé hors de l'état courant.</summary>
    private sealed record LibrarySnapshot(
        IReadOnlyList<SpaceInfo> Spaces,
        long SpaceId,
        IReadOnlyList<MemoryCard> Cards,
        IReadOnlyList<CollectionInfo> Albums,
        IReadOnlyList<CollectionInfo> Recaps,
        AppSettings Settings);

    private static async Task<LibrarySnapshot> LoadSnapshotAsync(
        MemoLibrary library,
        long preferredSpaceId,
        CancellationToken ct)
    {
        var spaces = await library.Catalog.GetSpacesAsync(ct);
        if (spaces.Count == 0)
        {
            var space = await library.Catalog.CreateSpaceAsync("Mon export", null, ct);
            spaces = new[] { space };
        }

        var spaceId = preferredSpaceId != 0 && spaces.Any(s => s.Id == preferredSpaceId)
            ? preferredSpaceId
            : spaces[0].Id;
        var cards = await library.Catalog.QueryAsync(
            new MemoryQuery(IncludeExcluded: true, IncludeUndated: true, Limit: int.MaxValue), ct);
        var albums = await library.Catalog.GetCollectionsAsync(CollectionKind.Album, ct);
        var recaps = await library.Catalog.GetCollectionsAsync(CollectionKind.Recap, ct);

        return new LibrarySnapshot(spaces, spaceId, cards, albums, recaps, library.Settings.Current);
    }

    private void ApplyLibrary(MemoLibrary library, LibrarySnapshot snapshot)
    {
        // Nouvelle génération avant toute écriture d'état : les continuations
        // de l'ancienne bibliothèque deviennent obsolètes immédiatement.
        _libraryEpoch++;
        Library = library;
        LibraryPath = library.Info.RootPath;
        LibraryName = library.Info.Name;
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(LibrarySnapshot snapshot)
    {
        Spaces = snapshot.Spaces;
        CurrentSpaceId = snapshot.SpaceId;
        Settings = snapshot.Settings;
        ReplaceAllCards(snapshot.Cards);
        Reset(Albums, snapshot.Albums);
        Reset(Recaps, snapshot.Recaps);
    }

    private bool IsCurrent(MemoLibrary library, int epoch)
        => _libraryEpoch == epoch && ReferenceEquals(Library, library);

    /// <summary>Relit une seule carte après une mutation qui peut affecter des champs dérivés.</summary>
    private async Task RefreshCardAsync(
        MemoLibrary library,
        int epoch,
        long memoryId,
        Func<MemoryCard, MemoryCard> fallback,
        CancellationToken ct)
    {
        MemoryCard? refreshed = null;
        try
        {
            refreshed = await library.Catalog.GetCardAsync(memoryId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // L'écriture a réussi : une lecture de contrôle en échec ne doit pas
            // faire échouer l'action. La projection locale sert de repli.
        }

        if (!IsCurrent(library, epoch))
        {
            return;
        }
        ReplaceCard(memoryId, current => refreshed ?? fallback(current));
    }

    /// <summary>Rafraîchit uniquement les albums et récapitulatifs (compteurs, dates, couvertures).</summary>
    private async Task RefreshCollectionsAsync(MemoLibrary library, int epoch, CancellationToken ct)
    {
        var albums = await library.Catalog.GetCollectionsAsync(CollectionKind.Album, ct);
        var recaps = await library.Catalog.GetCollectionsAsync(CollectionKind.Recap, ct);
        if (!IsCurrent(library, epoch))
        {
            return;
        }

        Reset(Albums, albums);
        Reset(Recaps, recaps);
    }

    private void ReplaceAllCards(IReadOnlyList<MemoryCard> cards)
    {
        Cards.Clear();
        _cardIndex.Clear();
        for (var i = 0; i < cards.Count; i++)
        {
            Cards.Add(cards[i]);
            _cardIndex[cards[i].Id] = i;
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

    private void ReplaceCard(long memoryId, Func<MemoryCard, MemoryCard> update)
    {
        if (_cardIndex.TryGetValue(memoryId, out var index) &&
            index >= 0 && index < Cards.Count && Cards[index].Id == memoryId)
        {
            Cards[index] = update(Cards[index]);
            return;
        }

        Replace(c => c.Id == memoryId, update);
    }

    private static void Reset<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <summary>Insère une collection en respectant l'ordre de GetCollectionsAsync (nom, id).</summary>
    private static void InsertByName(ObservableCollection<CollectionInfo> target, CollectionInfo item)
    {
        var index = 0;
        while (index < target.Count &&
               (string.Compare(target[index].Name, item.Name, StringComparison.OrdinalIgnoreCase) < 0 ||
                (string.Equals(target[index].Name, item.Name, StringComparison.OrdinalIgnoreCase) &&
                 target[index].Id < item.Id)))
        {
            index++;
        }
        target.Insert(index, item);
    }
}
