using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Memo.Core;

namespace Memo.App.Services;

/// <summary>
/// Regroupe localement des souvenirs datés en propositions d'événements.
///
/// Le service ne lit ni n'écrit de données de compte : il ne consulte que les
/// cartes déjà chargées, les dates de la bibliothèque et les lieux explicitement
/// associés à un souvenir. Aucun historique de localisation n'est utilisé et
/// aucune séquence vidéo n'est fusionnée.
/// </summary>
public sealed class EventSuggestionService
{
    public static EventSuggestionService Current { get; } = new();

    private const int MinimumMemories = 2;
    private const int DemoEventIdStart = 10_000;

    /// <summary>Deux souvenirs séparés de six heures ou plus ne forment pas le même événement.</summary>
    private static readonly TimeSpan MaximumGap = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly AppState _state = AppState.Current;
    private readonly ConcurrentDictionary<(long Id, DateOnly? Date), DateTimeOffset?> _timestamps = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly Dictionary<long, DemoEvent> _demoEvents = new();

    private LocalStateDocument? _document;
    private LibraryState? _libraryState;
    private LibraryState? _demoState;
    private string? _stateKey;
    private string? _resetKey;
    private long _nextDemoId = DemoEventIdStart;

    private EventSuggestionService()
    {
        _resetKey = LibraryKey;
        _state.LibraryChanged += (_, _) => OnLibraryChanged();
    }

    /// <summary>Vrai si des propositions ont été ignorées pour la bibliothèque courante.</summary>
    public bool HasIgnored =>
        _state.Library is null
            ? (_demoState?.Ignored.Count ?? 0) > 0
            : (_libraryState?.Ignored.Count ?? 0) > 0;

    /// <summary>
    /// Construit les propositions à partir des cartes visibles. Les souvenirs
    /// masqués, sans date suffisamment précise ou sans média principal sont
    /// écartés. Une proposition déjà ignorée ou enregistrée n'est pas répétée.
    /// </summary>
    public async Task<IReadOnlyList<EventSuggestion>> BuildSuggestionsAsync(CancellationToken ct = default)
    {
        var libraryState = await EnsureLoadedAsync(ct);
        var handled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in libraryState.Ignored)
        {
            handled.Add(key);
        }
        foreach (var key in libraryState.Saved)
        {
            handled.Add(key);
        }

        var eligible = _state.Cards
            .Where(c => !c.IsExcluded)
            .Where(c => c.LocalDate is not null && c.DatePrecision >= DatePrecision.Day)
            .Where(c => c.Kind != MediaKind.Unknown && c.Status != MemoryStatus.OverlayOnly)
            .ToList();

        var results = new List<EventSuggestion>();
        foreach (var space in eligible.GroupBy(c => c.SpaceId))
        {
            var ordered = space
                .OrderBy(c => c.LocalDate)
                .ThenBy(c => c.Id)
                .ToList();

            foreach (var cluster in BuildDateClusters(ordered))
            {
                ct.ThrowIfCancellationRequested();
                if (cluster.Count < MinimumMemories)
                {
                    continue;
                }

                foreach (var group in await SplitByTimeAsync(cluster, ct))
                {
                    if (group.Count < MinimumMemories)
                    {
                        continue;
                    }

                    var suggestion = CreateSuggestion(group);
                    if (!handled.Contains(suggestion.Key))
                    {
                        results.Add(suggestion);
                    }
                }
            }
        }

        return results
            .OrderByDescending(s => s.EndDate)
            .ThenByDescending(s => s.StartDate)
            .ThenByDescending(s => s.MemoryCount)
            .ToList();
    }

    /// <summary>Mémorise localement le refus d'une proposition.</summary>
    public async Task IgnoreAsync(EventSuggestion suggestion, CancellationToken ct = default)
    {
        var libraryState = await EnsureLoadedAsync(ct);
        await _stateGate.WaitAsync(ct);
        try
        {
            if (!libraryState.Ignored.Contains(suggestion.Key, StringComparer.Ordinal))
            {
                libraryState.Ignored.Add(suggestion.Key);
                await SaveDocumentAsync(ct);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>Rend de nouveau visibles toutes les propositions ignorées.</summary>
    public async Task ClearIgnoredAsync(CancellationToken ct = default)
    {
        var libraryState = await EnsureLoadedAsync(ct);
        await _stateGate.WaitAsync(ct);
        try
        {
            if (libraryState.Ignored.Count > 0)
            {
                libraryState.Ignored.Clear();
                await SaveDocumentAsync(ct);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>
    /// Enregistre une proposition comme collection locale de type Event.
    /// Les éléments masqués entre-temps sont retirés ; aucun fichier n'est déplacé.
    /// </summary>
    public async Task<CollectionInfo> SaveAsCollectionAsync(
        EventSuggestion suggestion,
        string name,
        CancellationToken ct = default)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? suggestion.Title : name.Trim();
        var available = _state.Cards
            .Where(c => !c.IsExcluded)
            .Select(c => c.Id)
            .ToHashSet();
        var memories = suggestion.Memories
            .Where(c => available.Contains(c.Id))
            .ToList();
        var ids = memories.Select(c => c.Id).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new InvalidOperationException("Cette proposition ne contient plus de souvenir disponible.");
        }

        CollectionInfo collection;
        var library = _state.Library;
        if (library is null)
        {
            collection = new CollectionInfo(
                _nextDemoId++,
                cleanName,
                CollectionKind.Event,
                DateTimeOffset.Now,
                memories.Count,
                suggestion.CoverPath,
                suggestion.StartDate,
                suggestion.EndDate);
            _demoEvents[collection.Id] = new DemoEvent(collection, memories);
        }
        else
        {
            collection = await library.Catalog.CreateCollectionAsync(cleanName, CollectionKind.Event, ct);
            await library.Catalog.AddToCollectionAsync(collection.Id, ids, ct);
            collection = collection with
            {
                ItemCount = memories.Count,
                CoverThumbnail = suggestion.CoverPath,
                FirstDate = suggestion.StartDate,
                LastDate = suggestion.EndDate,
            };
        }

        await MarkSavedAsync(suggestion, collection.Id, ct);
        return collection;
    }

    /// <summary>Liste les collections d'événements existantes, sans les modifier.</summary>
    public async Task<IReadOnlyList<CollectionInfo>> GetSavedEventsAsync(CancellationToken ct = default)
    {
        var library = _state.Library;
        if (library is null)
        {
            return _demoEvents.Values
                .OrderByDescending(e => e.Info.CreatedAt)
                .Select(e => e.Info)
                .ToList();
        }

        return await library.Catalog.GetCollectionsAsync(CollectionKind.Event, ct);
    }

    /// <summary>Charge les souvenirs d'une collection d'événements.</summary>
    public async Task<IReadOnlyList<MemoryCard>> GetSavedEventItemsAsync(
        CollectionInfo collection,
        CancellationToken ct = default)
    {
        var library = _state.Library;
        if (library is null)
        {
            return _demoEvents.TryGetValue(collection.Id, out var demo)
                ? demo.Items
                : Array.Empty<MemoryCard>();
        }

        return await library.Catalog.GetCollectionItemsAsync(collection.Id, ct);
    }

    /// <summary>Renomme une collection d'événements déjà enregistrée.</summary>
    public async Task RenameSavedEventAsync(
        CollectionInfo collection,
        string name,
        CancellationToken ct = default)
    {
        var cleanName = name?.Trim();
        if (string.IsNullOrEmpty(cleanName))
        {
            throw new ArgumentException("Le nom de la collection est obligatoire.", nameof(name));
        }

        var library = _state.Library;
        if (library is null)
        {
            if (!_demoEvents.TryGetValue(collection.Id, out var demo))
            {
                throw new KeyNotFoundException($"Événement {collection.Id} introuvable.");
            }
            demo.Info = demo.Info with { Name = cleanName };
            return;
        }

        await library.Catalog.RenameCollectionAsync(collection.Id, cleanName, ct);
    }

    /// <summary>Supprime la collection d'événements et libère sa proposition.</summary>
    public async Task DeleteSavedEventAsync(CollectionInfo collection, CancellationToken ct = default)
    {
        var library = _state.Library;
        if (library is null)
        {
            _demoEvents.Remove(collection.Id);
        }
        else
        {
            await library.Catalog.DeleteCollectionAsync(collection.Id, ct);
        }

        var libraryState = await EnsureLoadedAsync(ct);
        await _stateGate.WaitAsync(ct);
        try
        {
            if (libraryState.SavedKeyByCollection.TryGetValue(collection.Id, out var key))
            {
                libraryState.SavedKeyByCollection.Remove(collection.Id);
                libraryState.Saved.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
                await SaveDocumentAsync(ct);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task MarkSavedAsync(EventSuggestion suggestion, long collectionId, CancellationToken ct)
    {
        var libraryState = await EnsureLoadedAsync(ct);
        await _stateGate.WaitAsync(ct);
        try
        {
            if (!libraryState.Saved.Contains(suggestion.Key, StringComparer.Ordinal))
            {
                libraryState.Saved.Add(suggestion.Key);
            }
            libraryState.SavedKeyByCollection[collectionId] = suggestion.Key;
            await SaveDocumentAsync(ct);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>
    /// Première passe : regroupe par espace puis par journée ou week-end.
    /// Aucun regroupement entre deux espaces ou deux week-ends distincts.
    /// </summary>
    private static List<List<MemoryCard>> BuildDateClusters(IReadOnlyList<MemoryCard> ordered)
    {
        var clusters = new List<List<MemoryCard>>();
        List<MemoryCard>? current = null;
        DateOnly? previousDate = null;

        foreach (var card in ordered)
        {
            var date = card.LocalDate!.Value;
            if (current is not null && previousDate is { } previous && CanShareDayCluster(previous, date))
            {
                current.Add(card);
            }
            else
            {
                current = new List<MemoryCard> { card };
                clusters.Add(current);
            }

            previousDate = date;
        }

        return clusters;
    }

    /// <summary>Même jour, ou vendredi/samedi/dimanche du même week-end.</summary>
    private static bool CanShareDayCluster(DateOnly first, DateOnly second)
    {
        if (first == second)
        {
            return true;
        }

        var firstAnchor = WeekendAnchor(first);
        return firstAnchor is not null && firstAnchor == WeekendAnchor(second);
    }

    private static DateOnly? WeekendAnchor(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Friday => date.AddDays(1),
        DayOfWeek.Saturday => date,
        DayOfWeek.Sunday => date.AddDays(-1),
        _ => null,
    };

    /// <summary>
    /// Deuxième passe : coupe une journée ou un week-end quand deux heures
    /// connues sont séparées de six heures ou plus. Si l'heure manque, la date
    /// reste le seul critère et le groupe n'est pas coupé artificiellement.
    /// </summary>
    private async Task<List<List<MemoryCard>>> SplitByTimeAsync(
        List<MemoryCard> cluster,
        CancellationToken ct)
    {
        var entries = new List<TimeEntry>(cluster.Count);
        foreach (var card in cluster)
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(await ResolveEntryAsync(card, ct));
        }

        entries.Sort(CompareEntries);

        var groups = new List<List<MemoryCard>>();
        List<MemoryCard>? current = null;
        TimeEntry? previous = null;

        foreach (var entry in entries)
        {
            var split = false;
            if (previous is { } before &&
                before.Instant is { } beforeInstant &&
                entry.Instant is { } entryInstant)
            {
                split = entryInstant - beforeInstant >= MaximumGap;
            }

            if (current is null || split)
            {
                current = new List<MemoryCard>();
                groups.Add(current);
            }

            current.Add(entry.Card);
            previous = entry;
        }

        return groups;
    }

    private async Task<TimeEntry> ResolveEntryAsync(MemoryCard card, CancellationToken ct)
    {
        var instant = await ResolveInstantAsync(card, ct);
        TimeOnly? localTime = null;
        if (instant is { } value)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(_state.Settings.TimeZoneId);
                localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
            }
            catch
            {
                // Un fuseau invalide ne doit pas empêcher la proposition.
                localTime = TimeOnly.FromDateTime(value.LocalDateTime);
            }
        }

        return new TimeEntry(card, instant, localTime);
    }

    private async Task<DateTimeOffset?> ResolveInstantAsync(MemoryCard card, CancellationToken ct)
    {
        var library = _state.Library;
        if (library is null ||
            card.DatePrecision != DatePrecision.Second ||
            card.DateSource == DateSource.User)
        {
            // Une date corrigée à la main ne porte qu'un jour, pas une heure fiable.
            return null;
        }

        var cacheKey = (card.Id, card.LocalDate);
        if (_timestamps.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        DateTimeOffset? resolved = null;
        try
        {
            var candidates = await library.Catalog.GetDateCandidatesAsync(card.Id, ct);
            var match = candidates
                .Where(c => c.LocalDate == card.LocalDate &&
                            c.InstantUtc is not null &&
                            c.Precision == DatePrecision.Second)
                .OrderByDescending(c => c.Precision)
                .ThenByDescending(c => c.Confidence)
                .FirstOrDefault();

            if (match?.InstantUtc is { } raw &&
                DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var instant))
            {
                resolved = instant;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Une date sans heure exploitable reste utilisable par la proposition.
            resolved = null;
        }

        _timestamps[cacheKey] = resolved;
        return resolved;
    }

    private static int CompareEntries(TimeEntry first, TimeEntry second)
    {
        var date = first.Card.LocalDate!.Value.CompareTo(second.Card.LocalDate!.Value);
        if (date != 0)
        {
            return date;
        }

        var firstTime = first.LocalTime ?? new TimeOnly(12, 0);
        var secondTime = second.LocalTime ?? new TimeOnly(12, 0);
        var time = firstTime.CompareTo(secondTime);
        return time != 0 ? time : first.Card.Id.CompareTo(second.Card.Id);
    }

    private EventSuggestion CreateSuggestion(List<MemoryCard> memories)
    {
        // L'ordre est déjà chronologique : il vient du tri par date puis par heure locale.
        var ordered = memories.ToList();
        var start = ordered[0].LocalDate!.Value;
        var end = ordered[^1].LocalDate!.Value;
        var weekend = IsWeekendSpan(start, end);
        var places = ExtractPlaces(ordered);
        var key = FormattableString.Invariant(
            $"{ordered[0].SpaceId}:{start:yyyyMMdd}:{end:yyyyMMdd}:{ordered[0].Id}:{ordered[^1].Id}");

        return new EventSuggestion
        {
            Key = key,
            Title = BuildTitle(start, end, places, weekend),
            StartDate = start,
            EndDate = end,
            Memories = ordered,
            Places = places,
            IsWeekend = weekend,
            CoverPath = _state.ThumbnailFor(ordered[0].Id),
        };
    }

    private static bool IsWeekendSpan(DateOnly start, DateOnly end)
    {
        if (start == end)
        {
            return start.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        }

        for (var day = start; day <= end; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Seuls les libellés explicitement associés aux souvenirs sont repris.
    /// Un souvenir sans lieu ne reçoit aucune position ni aucun libellé inventé.
    /// </summary>
    private static List<string> ExtractPlaces(IEnumerable<MemoryCard> memories)
    {
        var places = new List<string>();
        foreach (var card in memories)
        {
            var label = card.LocationLabel?.Trim();
            if (string.IsNullOrEmpty(label) ||
                places.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            places.Add(label);
        }

        return places;
    }

    private static string BuildTitle(
        DateOnly start,
        DateOnly end,
        IReadOnlyList<string> places,
        bool weekend)
    {
        var prefix = weekend ? "Week-end" : start == end ? "Journée" : "Séjour";

        if (places.Count == 1)
        {
            return $"{prefix} à {places[0]}";
        }
        if (places.Count == 2)
        {
            return $"{prefix} à {places[0]} et {places[1]}";
        }
        if (places.Count > 2)
        {
            return $"{prefix} dans {places.Count} lieux";
        }

        // Aucun lieu explicite : le titre ne mentionne qu'une période.
        if (weekend)
        {
            return $"Week-end du {LongDate(start)}";
        }
        if (start == end)
        {
            return $"Souvenirs du {LongDate(start)}";
        }
        return $"Du {start.ToDateTime(TimeOnly.MinValue).ToString("d MMM", AppState.French)} " +
               $"au {end.ToDateTime(TimeOnly.MinValue).ToString("d MMM yyyy", AppState.French)}";
    }

    private static string LongDate(DateOnly date)
        => date.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy", AppState.French);

    private async Task<LibraryState> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_state.Library is null)
        {
            return _demoState ??= new LibraryState();
        }

        await _stateGate.WaitAsync(ct);
        try
        {
            var key = LibraryKey;
            if (_document is null || !string.Equals(_stateKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _document = await LoadDocumentAsync(ct);
                _stateKey = key;
                if (!_document.Libraries.TryGetValue(key, out var state))
                {
                    state = new LibraryState();
                    _document.Libraries[key] = state;
                }
                _libraryState = state;
            }

            return _libraryState!;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task SaveDocumentAsync(CancellationToken ct)
    {
        if (_state.Library is null || _document is null)
        {
            return;
        }

        try
        {
            var path = StatePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, _document, JsonOptions, ct);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            // L'état local est une commodité : son échec ne bloque pas la page.
        }
        catch (UnauthorizedAccessException)
        {
            // Idem : les suggestions restent utilisables pour la session.
        }
    }

    private static async Task<LocalStateDocument> LoadDocumentAsync(CancellationToken ct)
    {
        try
        {
            var path = StatePath;
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                var document = await JsonSerializer.DeserializeAsync<LocalStateDocument>(stream, JsonOptions, ct);
                if (document is not null)
                {
                    var libraries = new Dictionary<string, LibraryState>(StringComparer.OrdinalIgnoreCase);
                    if (document.Libraries is not null)
                    {
                        foreach (var pair in document.Libraries)
                        {
                            pair.Value.Ignored ??= new List<string>();
                            pair.Value.Saved ??= new List<string>();
                            pair.Value.SavedKeyByCollection ??= new Dictionary<long, string>();
                            libraries[pair.Key] = pair.Value;
                        }
                    }
                    document.Libraries = libraries;
                    return document;
                }
            }
        }
        catch (IOException)
        {
            // Fichier absent ou illisible : on repart d'un état vide.
        }
        catch (UnauthorizedAccessException)
        {
            // Idem.
        }
        catch (JsonException)
        {
            // État corrompu : on repart d'un état vide sans toucher aux souvenirs.
        }

        return new LocalStateDocument();
    }

    private void OnLibraryChanged()
    {
        var key = LibraryKey;
        if (string.Equals(_resetKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _resetKey = key;
        _timestamps.Clear();
        _document = null;
        _libraryState = null;
        _stateKey = null;

        if (_state.Library is null)
        {
            _demoState = new LibraryState();
            _demoEvents.Clear();
            _nextDemoId = DemoEventIdStart;
        }
    }

    private string LibraryKey
    {
        get
        {
            var path = _state.LibraryPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return "demo";
            }

            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToLowerInvariant();
            }
            catch
            {
                return path.ToLowerInvariant();
            }
        }
    }

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Memo",
        "event-suggestions.json");

    private sealed record TimeEntry(MemoryCard Card, DateTimeOffset? Instant, TimeOnly? LocalTime);

    private sealed class DemoEvent(CollectionInfo info, IReadOnlyList<MemoryCard> items)
    {
        public CollectionInfo Info { get; set; } = info;
        public IReadOnlyList<MemoryCard> Items { get; } = items;
    }

    private sealed class LibraryState
    {
        public List<string> Ignored { get; set; } = new();
        public List<string> Saved { get; set; } = new();
        public Dictionary<long, string> SavedKeyByCollection { get; set; } = new();
    }

    private sealed class LocalStateDocument
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, LibraryState> Libraries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
