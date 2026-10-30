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
    public Guid? LibraryId => Library?.Info.Id;
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

    // Bibliothèque visée par une activation de notification. Tant qu'elle est
    // définie, l'ouverture automatique de la « dernière bibliothèque » par la
    // coquille ne doit pas remplacer la cible : le clic doit gagner, même s'il
    // arrive pendant le chargement initial.
    private ActivationTarget? _activationTarget;

    /// <summary>
    /// Cible d'activation encore valide. Une expiration de sécurité évite
    /// qu'une activation jamais aboutie ne bloque indéfiniment les ouvertures
    /// de bibliothèque.
    /// </summary>
    private ActivationTarget? ActiveActivation
    {
        get
        {
            var target = _activationTarget;
            if (target is not null && DateTimeOffset.UtcNow > target.ExpiresAt)
            {
                _activationTarget = null;
                return null;
            }

            return target;
        }
    }

    private AppState() { }

    public void LoadDemo()
    {
        // Une activation en cours ne doit pas être effacée par le repli de la
        // coquille (lecture de la dernière bibliothèque en échec).
        if (ActiveActivation is not null)
        {
            return;
        }

        LoadDemoCore();
    }

    private void LoadDemoCore()
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
    {
        // Une activation de notification est prioritaire sur l'ouverture
        // automatique de la dernière bibliothèque par la coquille. Pendant ce
        // court intervalle, une ouverture d'un autre chemin est redirigée vers
        // la bibliothèque visée plutôt que de l'écraser.
        var activation = ActiveActivation;
        if (activation is not null && !PathEquals(rootPath, activation.LibraryPath))
        {
            return EnsureActivationLibraryAsync(activation.LibraryId, activation.LibraryPath, ct);
        }

        return OpenLibraryCoreAsync(rootPath, activation?.LibraryId, ct);
    }

    private Task OpenLibraryAsync(string rootPath, Guid? expectedLibraryId, CancellationToken ct)
        => OpenLibraryCoreAsync(rootPath, expectedLibraryId, ct);

    private async Task OpenLibraryCoreAsync(
        string rootPath,
        Guid? expectedLibraryId,
        CancellationToken ct)
    {
        var full = Path.GetFullPath(rootPath);
        var current = Library;
        if (current is not null
            && PathEquals(current.Info.RootPath, full)
            && (expectedLibraryId is null || current.Info.Id == expectedLibraryId.Value))
        {
            // La bibliothèque demandée est déjà ouverte et entièrement chargée :
            // inutile de relire tout le catalogue (cas de la coquille qui
            // recharge la dernière bibliothèque au démarrage).
            return;
        }

        await SwitchLibraryAsync(
            token => MemoLibrary.OpenAsync(full, token),
            ct,
            expectedLibraryId);
    }

    public Task CreateLibraryAsync(string rootPath, string name, CancellationToken ct = default)
    {
        // Le fuseau courant sert de valeur initiale au nouveau catalogue.
        var timeZoneId = Settings.TimeZoneId;
        return SwitchLibraryAsync(
            token => MemoLibrary.CreateAsync(rootPath, name, timeZoneId, token),
            ct,
            expectedLibraryId: null);
    }

    public async Task CloseLibraryAsync()
    {
        await _libraryGate.WaitAsync();
        try
        {
            _activationTarget = null;
            var previous = Library;
            _libraryEpoch++;
            Library = null;
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }
            LoadDemoCore();
        }
        finally
        {
            _libraryGate.Release();
        }
    }

    /// <summary>
    /// Bascule atomique : la nouvelle bibliothèque est ouverte et son contenu
    /// est chargé avant de remplacer l'ancienne. En cas d'échec, l'état courant
    /// (et le chemin mémorisé) reste strictement inchangé. Si
    /// <paramref name="expectedLibraryId"/> est fourni, l'identifiant du
    /// manifeste doit correspondre : un chemin réutilisé par une autre
    /// bibliothèque est refusé avant toute bascule.
    /// </summary>
    private async Task SwitchLibraryAsync(
        Func<CancellationToken, Task<MemoLibrary>> open,
        CancellationToken ct,
        Guid? expectedLibraryId)
    {
        await _libraryGate.WaitAsync(ct);
        MemoLibrary? candidate = null;
        try
        {
            candidate = await open(ct);

            if (expectedLibraryId is Guid expected && candidate.Info.Id != expected)
            {
                throw new LibraryMismatchException(candidate.Info.RootPath, expected, candidate.Info.Id);
            }

            // Charge tout avant la bascule : Cards/Albums/Recaps ne sont jamais
            // vidés pour une bibliothèque qui ne s'ouvre pas entièrement.
            var snapshot = await LoadSnapshotAsync(candidate, 0, ct);

            var previous = Library;
            ApplyLibrary(candidate, snapshot);
            candidate = null; // l'instance appartient désormais à AppState

            // Dernière bibliothèque mémorisée uniquement après succès complet.
            // L'identifiant est conservé pour retrouver la bibliothèque d'une
            // notification même si le chemin mémorisé a changé entre-temps.
            RememberLibrary(LibraryId!.Value, LibraryPath!, LibraryName);

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
    /// Mémorise la bibliothèque visée par une activation de notification avant
    /// que la fenêtre ne charge la dernière bibliothèque. L'identifiant est
    /// l'information de référence ; le chemin n'est qu'un indice.
    /// </summary>
    public void BeginActivation(Guid? libraryId, string? fallbackPath)
    {
        var path = NormalizePath(fallbackPath);
        if (libraryId is Guid id && path is null)
        {
            path = FindKnownLibraryPath(id) ?? NormalizePath(ReadLastLibrary());
        }

        _activationTarget = new ActivationTarget(
            libraryId,
            path,
            DateTimeOffset.UtcNow.AddMinutes(2));
    }

    /// <summary>
    /// Ouvre la bibliothèque visée par une notification et attend la fin
    /// complète de son chargement. L'identifiant du manifeste est vérifié :
    /// si le chemin mémorisé désigne désormais une autre bibliothèque, celle-ci
    /// n'est pas ouverte à la place de la bonne.
    /// </summary>
    public async Task EnsureActivationLibraryAsync(
        Guid? libraryId,
        string? fallbackPath,
        CancellationToken ct = default)
    {
        var activation = ActiveActivation;
        var effectiveId = libraryId ?? activation?.LibraryId;

        // Ancienne notification sans identifiant : la bibliothèque courante
        // reste la référence, sinon on retombe sur le dernier chemin connu.
        if (effectiveId is null)
        {
            if (Library is not null)
            {
                return;
            }

            var legacyPath = FirstPath(fallbackPath, activation?.LibraryPath, ReadLastLibrary());
            if (legacyPath is null)
            {
                throw new LibraryActivationException(
                    "Cette notification ne désigne aucune bibliothèque connue. " +
                    "Ouvrez la bibliothèque depuis Réglages, puis cliquez à nouveau sur la notification.");
            }

            await OpenLibraryAsync(legacyPath, expectedLibraryId: null, ct);
            return;
        }

        // La bonne bibliothèque est déjà ouverte et chargée.
        if (Library is not null && Library.Info.Id == effectiveId.Value)
        {
            return;
        }

        // L'identifiant prime sur le chemin. On essaie d'abord le chemin
        // mémorisé pour cet identifiant, puis les indices de la charge utile et
        // enfin la dernière bibliothèque ouverte.
        var candidates = new List<string>();
        AddPath(candidates, FindKnownLibraryPath(effectiveId.Value));
        AddPath(candidates, fallbackPath);
        AddPath(candidates, activation?.LibraryPath);
        AddPath(candidates, ReadLastLibrary());
        AddPath(candidates, LibraryPath);

        var problems = new List<string>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(candidate))
            {
                problems.Add($"« {candidate} » est introuvable");
                continue;
            }

            try
            {
                await OpenLibraryAsync(candidate, effectiveId, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LibraryMismatchException ex)
            {
                problems.Add(ex.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problems.Add($"« {candidate} » : {ex.Message}");
            }
        }

        var detail = problems.Count == 0
            ? "Aucun chemin connu ne correspond à cette bibliothèque."
            : string.Join(" ", problems);
        throw new LibraryActivationException(
            $"La bibliothèque visée par cette notification (identifiant {effectiveId.Value:D}) " +
            $"est introuvable ou ne correspond plus au chemin mémorisé. {detail} " +
            "Ouvrez-la depuis Réglages, puis cliquez à nouveau sur la notification.");
    }

    /// <summary>Libère la priorité d'activation une fois le jour affiché.</summary>
    public void CompleteActivation() => _activationTarget = null;

    private static string? FirstPath(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = NormalizePath(candidate);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return null;
    }

    private static void AddPath(List<string> paths, string? candidate)
    {
        var normalized = NormalizePath(candidate);
        if (normalized is not null
            && !paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(normalized);
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathEquals(string? left, string? right)
    {
        var a = NormalizePath(left);
        var b = NormalizePath(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
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

        // Le verrou couvre l'import, le rechargement et la préparation des
        // miniatures : une seconde opération longue sur cette bibliothèque est
        // refusée avec un message explicite, alors que les lectures restent
        // disponibles. ImportService acquiert le même verrou de façon
        // réentrante pour les appelants directs.
        using var operation = LibraryOperationGate.Acquire(
            library.Info.RootPath,
            LibraryOperationKind.Import);

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
        var library = Library;
        if (library is null)
        {
            Settings = Settings with { TimeZoneId = timeZoneId };
            return;
        }

        // Rien n'est modifié si une autre opération longue est déjà en cours :
        // le réglage en mémoire et le catalogue restent cohérents.
        using var operation = LibraryOperationGate.Acquire(
            library.Info.RootPath,
            LibraryOperationKind.TimeZoneChange);

        var epoch = _libraryEpoch;
        Settings = Settings with { TimeZoneId = timeZoneId };
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
    public async Task RestoreAsync(
        string zipPath,
        string destinationRoot,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // La restauration écrit un dossier complet et peut durer longtemps :
        // elle participe au même verrou, indexé par le dossier de destination.
        using var operation = LibraryOperationGate.Acquire(
            destinationRoot,
            LibraryOperationKind.Restore);

        await BackupRestoreService.RestoreAsync(zipPath, destinationRoot, progress, ct);
    }

    /// <summary>
    /// Acquiert le verrou unique pour une analyse locale (OCR, empreinte
    /// visuelle, visages). À libérer avec <c>using</c> à la fin de la passe.
    /// Une analyse lancée pendant un import ou un changement de fuseau reçoit
    /// un message explicite au lieu de s'exécuter en parallèle.
    /// </summary>
    public LibraryOperationLease AcquireAnalysisLease()
    {
        var library = Library
            ?? throw new InvalidOperationException("Aucune bibliothèque n'est ouverte.");
        return LibraryOperationGate.Acquire(
            library.Info.RootPath,
            LibraryOperationKind.Analysis);
    }

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

    private static readonly JsonSerializerOptions AppStateJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string LastLibraryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Memo", "app.json");

    /// <summary>
    /// Mémorise la dernière bibliothèque ouverte. L'identifiant et le nom sont
    /// conservés dans un index local pour retrouver une bibliothèque par son
    /// identifiant, même si le chemin mémorisé a changé depuis.
    /// </summary>
    public void RememberLibrary(string path)
    {
        var full = NormalizePath(path) ?? path;
        UpdateAppDocument(document =>
        {
            document.LastLibrary = full;
            if (Library is not null && PathEquals(Library.Info.RootPath, full))
            {
                UpsertLibrary(document, Library.Info.Id, full, Library.Info.Name);
            }
        });
    }

    private void RememberLibrary(Guid id, string path, string name)
    {
        var full = NormalizePath(path) ?? path;
        UpdateAppDocument(document =>
        {
            document.LastLibrary = full;
            UpsertLibrary(document, id, full, name);
        });
    }

    /// <summary>
    /// Enregistre seulement la correspondance identifiant → chemin, sans
    /// changer la dernière bibliothèque ouverte. Utilisé par le planificateur
    /// et le runner de rappels pour qu'un clic puisse retrouver la bibliothèque
    /// par son identifiant, même si elle n'a jamais été ouverte depuis la mise
    /// à jour.
    /// </summary>
    public void RememberLibraryLocation(Guid id, string path, string? name = null)
    {
        if (id == Guid.Empty)
        {
            return;
        }

        var full = NormalizePath(path) ?? path;
        UpdateAppDocument(document => UpsertLibrary(document, id, full, name));
    }

    public static string? ReadLastLibrary() => ReadAppDocument().LastLibrary;

    /// <summary>
    /// Chemin le plus récemment connu pour une bibliothèque donnée, ou null.
    /// Sert à résoudre une activation de notification par identifiant.
    /// </summary>
    public static string? FindKnownLibraryPath(Guid libraryId)
    {
        var document = ReadAppDocument();
        foreach (var entry in document.Libraries
                     .Where(entry => entry.Id == libraryId && !string.IsNullOrWhiteSpace(entry.Path))
                     .OrderByDescending(entry => entry.LastOpenedAt))
        {
            var path = NormalizePath(entry.Path);
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }

    private static void UpsertLibrary(AppDocument document, Guid id, string path, string? name)
    {
        // Un enregistrement sans nom (planificateur, runner) ne doit pas
        // effacer un nom déjà connu.
        var knownName = document.Libraries.FirstOrDefault(entry => entry.Id == id)?.Name;

        // Une même bibliothèque ne doit apparaître qu'une fois par identifiant
        // ou par chemin, même après un déplacement ou un renommage de dossier.
        document.Libraries.RemoveAll(entry =>
            entry.Id == id || PathEquals(entry.Path, path));

        document.Libraries.Add(new LibraryEntry
        {
            Id = id,
            Path = path,
            Name = name ?? knownName,
            LastOpenedAt = DateTimeOffset.UtcNow,
        });

        // Index borné : les bibliothèques anciennes restent retrouvables via
        // leur chemin direct sans faire grossir indéfiniment app.json.
        if (document.Libraries.Count > 12)
        {
            document.Libraries = document.Libraries
                .OrderByDescending(entry => entry.LastOpenedAt)
                .Take(12)
                .ToList();
        }
    }

    private static AppDocument ReadAppDocument()
    {
        try
        {
            var file = LastLibraryPath;
            if (!File.Exists(file))
            {
                return new AppDocument();
            }

            using var json = JsonDocument.Parse(File.ReadAllText(file));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new AppDocument();
            }

            var document = new AppDocument();
            if (TryGetProperty(json.RootElement, "lastLibrary", out var lastLibrary)
                && lastLibrary.ValueKind == JsonValueKind.String)
            {
                document.LastLibrary = lastLibrary.GetString();
            }

            if (TryGetProperty(json.RootElement, "libraries", out var libraries)
                && libraries.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in libraries.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !TryGetProperty(item, "id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(idElement.GetString(), out var id)
                        || id == Guid.Empty
                        || !TryGetProperty(item, "path", out var pathElement)
                        || pathElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var path = pathElement.GetString();
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    var name = TryGetProperty(item, "name", out var nameElement)
                        && nameElement.ValueKind == JsonValueKind.String
                            ? nameElement.GetString()
                            : null;
                    var lastOpenedAt = DateTimeOffset.MinValue;
                    if (TryGetProperty(item, "lastOpenedAt", out var openedElement)
                        && openedElement.ValueKind == JsonValueKind.String)
                    {
                        DateTimeOffset.TryParse(
                            openedElement.GetString(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind,
                            out lastOpenedAt);
                    }

                    document.Libraries.Add(new LibraryEntry
                    {
                        Id = id,
                        Path = path,
                        Name = name,
                        LastOpenedAt = lastOpenedAt,
                    });
                }
            }

            return document;
        }
        catch
        {
            // Un app.json absent ou illisible retombe sur un document vide.
            return new AppDocument();
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static readonly object AppDocumentGate = new();

    /// <summary>
    /// Lecture-modification-écriture sérialisée dans le processus : deux
    /// opérations concurrentes (ouverture de bibliothèque, runner de rappels)
    /// ne se perdent pas mutuellement. Entre processus, l'écriture atomique
    /// reste « dernier gagnant ».
    /// </summary>
    private static void UpdateAppDocument(Action<AppDocument> update)
    {
        try
        {
            lock (AppDocumentGate)
            {
                var document = ReadAppDocument();
                update(document);
                WriteAppDocument(document);
            }
        }
        catch
        {
            // mémorisation best-effort : un app.json invalide ne doit jamais
            // empêcher d'ouvrir une bibliothèque.
        }
    }

    private static void WriteAppDocument(AppDocument document)
    {
        var file = LastLibraryPath;
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporary, JsonSerializer.Serialize(document, AppStateJson));
            File.Move(temporary, file, overwrite: true);
        }
        catch
        {
            // mémorisation best-effort
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class AppDocument
    {
        public string? LastLibrary { get; set; }
        public List<LibraryEntry> Libraries { get; set; } = new();
    }

    private sealed class LibraryEntry
    {
        public Guid Id { get; set; }
        public string? Path { get; set; }
        public string? Name { get; set; }
        public DateTimeOffset LastOpenedAt { get; set; }
    }

    /// <summary>Bibliothèque visée par une activation de notification.</summary>
    private sealed record ActivationTarget(
        Guid? LibraryId,
        string? LibraryPath,
        DateTimeOffset ExpiresAt);

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

/// <summary>
/// Le chemin mémorisé contient une autre bibliothèque que celle visée par une
/// notification. L'ouverture est refusée avant toute bascule d'état.
/// </summary>
public sealed class LibraryMismatchException : InvalidOperationException
{
    public LibraryMismatchException(string path, Guid expected, Guid actual)
        : base($"Le chemin « {path} » contient la bibliothèque {actual:D}, " +
               $"alors que l'opération vise {expected:D}. " +
               "La bibliothèque trouvée n'a pas été ouverte.")
    {
        Path = path;
        Expected = expected;
        Actual = actual;
    }

    public string Path { get; }
    public Guid Expected { get; }
    public Guid Actual { get; }
}

/// <summary>
/// Aucune bibliothèque ne correspond à l'activation : le message est destiné à
/// être affiché à l'utilisateur.
/// </summary>
public sealed class LibraryActivationException : InvalidOperationException
{
    public LibraryActivationException(string message) : base(message)
    {
    }
}
