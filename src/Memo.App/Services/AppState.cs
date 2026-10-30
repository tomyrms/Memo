using System.Collections.ObjectModel;
using Memo.Core;

namespace Memo.App.Services;

/// <summary>
/// État applicatif partagé par les pages. En mode démonstration, il fournit
/// un jeu synthétique explicite ; une fois la bibliothèque ouverte, les mêmes
/// propriétés sont alimentées par Memo.Core.
/// </summary>
public sealed class AppState
{
    public static AppState Current { get; } = new();

    public bool IsDemo { get; private set; } = true;
    public string LibraryName { get; private set; } = "Bibliothèque de démonstration";
    public string? LibraryPath { get; private set; }

    public ObservableCollection<MemoryCard> Cards { get; } = new();
    public ObservableCollection<CollectionInfo> Albums { get; } = new();
    public AppSettings Settings { get; set; } = new(
        "Europe/Zurich", 4, true, true, false, false, "09:00", true,
        true, "System", false, false);

    public event EventHandler? LibraryChanged;

    private readonly Dictionary<long, string> _thumbnails = new();
    private readonly Dictionary<long, string> _media = new();
    private readonly Dictionary<long, string> _overlays = new();

    private AppState() { }

    public void LoadDemo()
    {
        IsDemo = true;
        LibraryName = "Bibliothèque de démonstration";
        LibraryPath = null;
        Cards.Clear();
        Albums.Clear();
        _thumbnails.Clear();
        _media.Clear();
        _overlays.Clear();

        foreach (var card in DemoData.CreateCards())
        {
            Cards.Add(card);
        }
        DemoData.FillAssets(_thumbnails, _media, _overlays);
        foreach (var album in DemoData.CreateAlbums())
        {
            Albums.Add(album);
        }
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MemoryCard> ThisDay(DateOnly day)
        => Cards
            .Where(c => c.LocalDate is { } d && d.Month == day.Month && d.Day == day.Day && d.Year < day.Year)
            .OrderByDescending(c => c.LocalDate)
            .ToList();

    public IReadOnlyList<MemoryCard> OneYearAgo(DateOnly day)
        => Cards
            .Where(c => c.LocalDate is { } d && d.Year == day.Year - 1 && d.Month == day.Month && d.Day == day.Day)
            .ToList();

    public IReadOnlyList<MemoryCard> Undated()
        => Cards.Where(c => c.LocalDate is null).ToList();

    public string? ThumbnailFor(long id) => _thumbnails.GetValueOrDefault(id);
    public string? MediaFor(long id) => _media.GetValueOrDefault(id);
    public string? OverlayFor(long id) => _overlays.GetValueOrDefault(id);

    public string FormatDate(MemoryCard card)
        => card.LocalDate is { } d ? d.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy", French) : "Date inconnue";

    public static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
}
