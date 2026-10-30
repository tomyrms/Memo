using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Memo.App.Pages;

public sealed partial class HomePage : Page
{
    // Historique des propositions en mode démonstration, qui n'a pas de catalogue.
    private static readonly List<(long Id, DateTimeOffset At)> DemoSuggestions = new();

    private static int? _lastSurpriseYear;

    private readonly StackPanel _dayCardHost = new();

    private DateOnly? _shownDay;
    private bool _surpriseRunning;

    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        Root.Children.Clear();
        var state = AppState.Current;
        var today = AppState.Current.Today;

        var head = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Widgets.Title("À redécouvrir"));
        titles.Children.Add(Widgets.Secondary(today.ToDateTime(TimeOnly.MinValue).ToString("dddd d MMMM yyyy", AppState.French)));
        head.Children.Add(titles);

        var surprise = new Button
        {
            Content = "Surprends-moi",
            Style = Widgets.Style("MemoPrimaryButtonStyle"),
            VerticalAlignment = VerticalAlignment.Center
        };
        surprise.Click += (_, _) => Surprise();
        Grid.SetColumn(surprise, 1);
        head.Children.Add(surprise);
        Root.Children.Add(head);

        if (state.Cards.Count == 0)
        {
            Root.Children.Add(Widgets.EmptyState(
                "Votre bibliothèque est vide",
                "Importez des photos ou des vidéos pour voir apparaître les souvenirs du jour.",
                "Importer des médias",
                () => App.MainWindow?.Navigate("imports")));
            return;
        }

        var thisDay = state.ThisDay(today)
            .Where(c => c.Status != MemoryStatus.OverlayOnly)
            .GroupBy(c => c.LocalDate!.Value.Year)
            .OrderByDescending(g => g.Key)
            .ToList();

        Root.Children.Add(Widgets.SectionHeader("Ce jour-là", "Tout voir", () => OpenLibrary(today)));

        if (thisDay.Count == 0)
        {
            Root.Children.Add(Widgets.EmptyState(
                "Rien pour ce jour",
                "Aucun souvenir daté du " + today.ToString("d MMMM", AppState.French) + " dans les années précédentes. La bibliothèque reste accessible.",
                "Parcourir la bibliothèque",
                () => App.MainWindow?.Navigate("library")));
        }
        else
        {
            foreach (var year in thisDay.Take(4))
            {
                var age = today.Year - year.Key;
                var label = age == 1 ? "Il y a un an" : $"Il y a {age} ans";
                var years = year.ToList();
                var rowHead = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                rowHead.Children.Add(new TextBlock
                {
                    Text = label,
                    Style = Widgets.Style("MemoBodyTextStyle"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                rowHead.Children.Add(Widgets.Caption(year.Key.ToString()));
                Root.Children.Add(rowHead);
                Root.Children.Add(Widgets.TileRow(years, showDate: false, context: label));
            }
        }

        var undated = state.Undated();
        var overlayOnly = state.Cards.Count(c => c.Status == MemoryStatus.OverlayOnly);
        if (undated.Count > 0 || overlayOnly > 0)
        {
            Root.Children.Add(Widgets.SectionHeader("À compléter"));
            var text = $"{undated.Count} souvenir(s) sans date restent consultables. {overlayOnly} calque(s) attendent leur média.";
            var load = new Button
            {
                Content = "Ouvrir la vue Date inconnue",
                Style = Widgets.Style("MemoSecondaryButtonStyle"),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            load.Click += (_, _) => App.MainWindow?.Navigate("library");
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(Widgets.Secondary(text));
            panel.Children.Add(load);
            Root.Children.Add(Widgets.Card(panel, new Thickness(16)));
            Root.Children.Add(new Border { Height = 24 });
        }

        Root.Children.Add(Widgets.SectionHeader("Une journée à redécouvrir", "Changer", ChangeDay));
        Root.Children.Add(_dayCardHost);
        FillDayCard();

        if (state.Albums.Count > 0)
        {
            Root.Children.Add(Widgets.SectionHeader("Vos albums", "Tout voir", () => App.MainWindow?.Navigate("albums")));
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12
            };
            foreach (var album in state.Albums)
            {
                row.Children.Add(AlbumCard(album));
            }
            Root.Children.Add(Widgets.ScrollRow(row, "Vos albums"));
        }
    }

    private void OpenLibrary(DateOnly day)
    {
        App.MainWindow?.Navigate("library");
    }

    /// <summary>
    /// Remplace uniquement la journée affichée : le bouton « Changer » garde le focus
    /// et les autres sections ne sont pas reconstruites.
    /// </summary>
    private void ChangeDay() => FillDayCard();

    private void FillDayCard()
    {
        _dayCardHost.Children.Clear();
        var state = AppState.Current;
        var day = PickDay(state, state.Today, _shownDay);
        if (day is null)
        {
            _dayCardHost.Children.Add(Widgets.EmptyState(
                "Pas encore de journée à proposer",
                "Les souvenirs sans date ne reçoivent pas de fausse journée anniversaire. Ajoutez une date dans la bibliothèque pour enrichir cette sélection.",
                "Parcourir la bibliothèque",
                () => App.MainWindow?.Navigate("library")));
            return;
        }

        _shownDay = day.Value.Day;
        _dayCardHost.Children.Add(DayCard(day.Value.Day, day.Value.Cards));
    }

    /// <summary>
    /// Ouvre une Story sur une journée datée. Les suggestions récentes sont évitées
    /// lorsqu'une alternative existe, sans jamais proposer d'élément masqué, sans date
    /// ou réduit à un calque isolé.
    /// </summary>
    private async void Surprise()
    {
        if (_surpriseRunning)
        {
            return;
        }

        _surpriseRunning = true;
        try
        {
            var state = AppState.Current;
            var today = state.Today;
            var days = state.Cards
                .Where(c => IsDatedStoryCandidate(c) && c.LocalDate < today)
                .GroupBy(c => c.LocalDate!.Value)
                .Select(g => g.OrderBy(c => c.Id).ToList())
                .ToList();

            if (days.Count == 0)
            {
                ShowNotice("Aucun souvenir daté et disponible à proposer. Les éléments masqués, les calques seuls et les dates inconnues sont ignorés.");
                return;
            }

            var recent = await GetRecentSuggestionIdsAsync(state);

            // Une journée entièrement fraîche est préférée ; sinon on retire seulement
            // les souvenirs déjà proposés récemment, et on retombe sur la journée
            // complète si tout a déjà été vu.
            var freshDays = days.Where(d => d.All(c => !recent.Contains(c.Id))).ToList();
            if (freshDays.Count == 0)
            {
                freshDays = days.Where(d => d.Any(c => !recent.Contains(c.Id))).ToList();
            }
            if (freshDays.Count == 0)
            {
                freshDays = days;
            }

            // Éviter la journée déjà affichée dans la section, puis deux propositions
            // consécutives dans la même année, lorsqu'une autre période est disponible.
            var alternatives = freshDays;
            if (alternatives.Count > 1 && _shownDay is { } shown)
            {
                var withoutShown = alternatives
                    .Where(d => d[0].LocalDate != shown)
                    .ToList();
                if (withoutShown.Count > 0)
                {
                    alternatives = withoutShown;
                }
            }
            if (alternatives.Count > 1 && _lastSurpriseYear is { } previousYear)
            {
                var otherYears = alternatives
                    .Where(d => d[0].LocalDate!.Value.Year != previousYear)
                    .ToList();
                if (otherYears.Count > 0)
                {
                    alternatives = otherYears;
                }
            }
            freshDays = alternatives;

            var picked = freshDays[Random.Shared.Next(freshDays.Count)];
            _lastSurpriseYear = picked[0].LocalDate!.Value.Year;
            var items = picked.Where(c => !recent.Contains(c.Id)).ToList();
            if (items.Count == 0)
            {
                items = picked;
            }

            await RecordSuggestionAsync(state, items);
            App.MainWindow?.ShowStory(items, 0);
        }
        catch
        {
            ShowNotice("La sélection n'a pas pu être préparée. Réessayez depuis la bibliothèque.");
        }
        finally
        {
            _surpriseRunning = false;
        }
    }

    private static (DateOnly Day, List<MemoryCard> Cards)? PickDay(AppState state, DateOnly today, DateOnly? avoid)
    {
        var groups = state.Cards
            .Where(c => IsDatedStoryCandidate(c) && c.LocalDate < today)
            .GroupBy(c => c.LocalDate!.Value)
            .OrderByDescending(g => g.Count())
            .ThenBy(_ => Random.Shared.Next())
            .Take(4)
            .ToList();
        if (groups.Count == 0)
        {
            return null;
        }

        if (groups.Count > 1 && avoid is { } previous)
        {
            var alternatives = groups.Where(g => g.Key != previous).ToList();
            if (alternatives.Count > 0)
            {
                groups = alternatives;
            }
        }

        var pick = groups[Random.Shared.Next(groups.Count)];
        return (pick.Key, pick.OrderBy(c => c.Id).ToList());
    }

    /// <summary>
    /// Seuls les souvenirs consultables, datés au jour près et non masqués participent
    /// aux sélections de type anniversaire.
    /// </summary>
    private static bool IsDatedStoryCandidate(MemoryCard card)
        => !card.IsExcluded
            && card.Status != MemoryStatus.OverlayOnly
            && card.LocalDate is not null
            && card.DatePrecision >= DatePrecision.Day;

    private static async Task<HashSet<long>> GetRecentSuggestionIdsAsync(AppState state)
    {
        if (state.Library is not null)
        {
            try
            {
                return (await state.Library.Catalog.GetRecentSuggestionIdsAsync(14)).ToHashSet();
            }
            catch
            {
                // L'historique est un confort : une erreur de catalogue ne bloque pas la sélection.
                return new HashSet<long>();
            }
        }

        var since = DateTimeOffset.UtcNow.AddDays(-14);
        DemoSuggestions.RemoveAll(s => s.At < since);
        return DemoSuggestions.Select(s => s.Id).ToHashSet();
    }

    private static async Task RecordSuggestionAsync(AppState state, IReadOnlyList<MemoryCard> cards)
    {
        var ids = cards.Select(c => c.Id).Distinct().ToList();
        if (state.Library is not null)
        {
            try
            {
                await state.Library.Catalog.RecordSuggestionAsync(ids);
            }
            catch
            {
                // L'historique est un confort : son échec ne doit pas empêcher la lecture.
            }
            return;
        }

        var now = DateTimeOffset.UtcNow;
        DemoSuggestions.RemoveAll(s => s.At < now.AddDays(-14));
        foreach (var id in ids)
        {
            DemoSuggestions.RemoveAll(s => s.Id == id);
            DemoSuggestions.Add((id, now));
        }
    }

    /// <summary>
    /// Bandeau refermable en haut de page, sans empiler plusieurs messages.
    /// </summary>
    private void ShowNotice(string message)
    {
        for (var i = Root.Children.Count - 1; i >= 0; i--)
        {
            if (Root.Children[i] is InfoBar { Tag: "surprise" } previous)
            {
                Root.Children.RemoveAt(i);
            }
        }

        var notice = new InfoBar
        {
            Title = "Surprends-moi",
            Message = message,
            Severity = InfoBarSeverity.Informational,
            IsClosable = true,
            IsOpen = true,
            Margin = new Thickness(0, 0, 0, 16),
            Tag = "surprise"
        };
        notice.Closed += (_, _) => Root.Children.Remove(notice);
        Root.Children.Insert(Math.Min(1, Root.Children.Count), notice);
    }

    private UIElement DayCard(DateOnly day, IReadOnlyList<MemoryCard> cards)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tile = Widgets.TileButton(cards[0], cards, 0, showDate: false);
        tile.Margin = new Thickness(0);
        grid.Children.Add(tile);

        var panel = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = day.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy", AppState.French),
            Style = Widgets.Style("MemoSubtitleTextStyle")
        });
        panel.Children.Add(Widgets.Secondary($"{cards.Count} souvenir(s) · {cards.Count(c => c.Kind == MediaKind.Video)} vidéo(s)"));
        panel.Children.Add(Widgets.Caption("Sélection issue de vos dates disponibles, hors éléments masqués."));
        var play = new Button
        {
            Content = "Lire la Story",
            Style = Widgets.Style("MemoPrimaryButtonStyle"),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        play.Click += (_, _) => App.MainWindow?.ShowStory(cards, 0);
        panel.Children.Add(play);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);

        return Widgets.Card(grid, new Thickness(16));
    }

    private static UIElement AlbumCard(CollectionInfo album)
    {
        var panel = new StackPanel { Spacing = 8, Width = 196 };
        var cover = new Border
        {
            Height = 112,
            CornerRadius = new CornerRadius(4),
            Background = Widgets.Brush("MemoPhotoPlaceholderBrush")
        };
        if (album.CoverThumbnail is not null)
        {
            cover.Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(album.CoverThumbnail)),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill
            };
        }
        panel.Children.Add(cover);
        panel.Children.Add(new TextBlock
        {
            Text = album.Name,
            Style = Widgets.Style("MemoBodyTextStyle"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(Widgets.Caption($"{album.ItemCount} souvenir(s)"));

        var button = new Button
        {
            Content = panel,
            Style = Widgets.Style("MemoTileButtonStyle"),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(button, $"Album {album.Name}, {album.ItemCount} souvenir(s)");
        button.Click += (_, _) => App.MainWindow?.Navigate("albums");
        return button;
    }
}
