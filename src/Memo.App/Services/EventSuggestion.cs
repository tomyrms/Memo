using Memo.Core;

namespace Memo.App.Services;

/// <summary>
/// Proposition d'événement calculée localement à partir des dates des souvenirs.
/// C'est une suggestion, jamais une catégorie de compte : aucun média n'est
/// déplacé, renommé ou fusionné et aucune position n'est inventée.
/// </summary>
public sealed class EventSuggestion
{
    public required string Key { get; init; }

    /// <summary>Titre proposé, construit sans déduire de lieu absent.</summary>
    public required string Title { get; init; }

    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required IReadOnlyList<MemoryCard> Memories { get; init; }

    /// <summary>Libellés de lieux explicitement associés à au moins un souvenir.</summary>
    public required IReadOnlyList<string> Places { get; init; }

    public required bool IsWeekend { get; init; }
    public string? CoverPath { get; init; }

    public MemoryCard Cover => Memories[0];
    public int MemoryCount => Memories.Count;
    public int PhotoCount => Memories.Count(c => c.Kind == MediaKind.Photo);
    public int VideoCount => Memories.Count(c => c.Kind == MediaKind.Video);
    public bool HasPlaces => Places.Count > 0;

    public string PeriodLabel => FormatPeriod(StartDate, EndDate);

    public string CountLabel =>
        $"{MemoryCount} souvenir(s) · {PhotoCount} photo(s) · {VideoCount} vidéo(s)";

    public string PlaceLabel => Places.Count switch
    {
        0 => "Aucun lieu explicite associé à ces souvenirs",
        1 => $"Lieu connu : {Places[0]}",
        _ => $"Lieux connus : {string.Join(", ", Places)}",
    };

    public static string FormatPeriod(DateOnly start, DateOnly end)
    {
        if (start == end)
        {
            return Capitalize(start.ToDateTime(TimeOnly.MinValue).ToString("dddd d MMMM yyyy", AppState.French));
        }

        if (start.Year == end.Year && start.Month == end.Month)
        {
            var month = start.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", AppState.French);
            return $"{start.Day}–{end.Day} {month}";
        }

        if (start.Year == end.Year)
        {
            return $"{start.ToDateTime(TimeOnly.MinValue).ToString("d MMM", AppState.French)} – " +
                   $"{end.ToDateTime(TimeOnly.MinValue).ToString("d MMM yyyy", AppState.French)}";
        }

        return $"{start.ToDateTime(TimeOnly.MinValue).ToString("d MMM yyyy", AppState.French)} – " +
               $"{end.ToDateTime(TimeOnly.MinValue).ToString("d MMM yyyy", AppState.French)}";
    }

    private static string Capitalize(string text)
        => string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], AppState.French) + text[1..];
}
