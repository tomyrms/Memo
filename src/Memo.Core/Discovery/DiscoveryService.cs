using System.Globalization;

namespace Memo.Core;

public sealed class DiscoveryService : IDiscoveryService
{
    private readonly MemoCatalog _catalog;

    internal DiscoveryService(MemoCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task<ThisDayResult> GetThisDayAsync(
        DateOnly day,
        ThisDayOptions options,
        CancellationToken ct = default)
    {
        return await _catalog.QueryThisDayAsync(day, options, ct).ConfigureAwait(false);
    }

    public async Task<SurpriseResult> SurpriseAsync(
        int count,
        IReadOnlyCollection<long> excludedIds,
        CancellationToken ct = default)
    {
        if (count <= 0)
        {
            return new SurpriseResult(Array.Empty<MemoryCard>(), null, "Aucun souvenir à redécouvrir.");
        }

        var excluded = (excludedIds ?? Array.Empty<long>()).ToHashSet();
        var days = await _catalog.GetDatedDaysAsync(excluded, ct).ConfigureAwait(false);
        if (days.Count == 0)
        {
            return new SurpriseResult(
                Array.Empty<MemoryCard>(),
                null,
                "Aucun souvenir daté à redécouvrir.");
        }

        // Une journée déjà proposée récemment n'est choisie que s'il n'existe aucune alternative.
        var freshDays = days.Where(day => day.NonRecent > 0).ToList();
        var pool = freshDays.Count > 0 ? freshDays : days;
        var picked = pool[Random.Shared.Next(pool.Count)];

        var cards = await _catalog.GetDayCardsAsync(picked.Day, excluded, ct).ConfigureAwait(false);
        var recent = (await _catalog.GetRecentSuggestionIdsAsync(14, ct).ConfigureAwait(false)).ToHashSet();
        var freshCards = cards.Where(card => !recent.Contains(card.Id)).ToList();
        var selected = (freshCards.Count > 0 ? freshCards : cards)
            .Take(count)
            .ToList();

        if (selected.Count > 0)
        {
            await _catalog.RecordSuggestionAsync(selected.Select(card => card.Id).ToList(), ct)
                .ConfigureAwait(false);
        }

        var label = "Souvenirs du " + picked.Day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return new SurpriseResult(selected, picked.Day, label);
    }
}
