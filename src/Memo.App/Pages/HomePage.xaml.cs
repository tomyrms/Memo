using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Memo.App.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        Root.Children.Clear();
        var state = AppState.Current;
        var today = DateOnly.FromDateTime(DateTime.Now);

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

        var thisDay = state.ThisDay(today)
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
                Root.Children.Add(Widgets.TileRow(years, showDate: false));
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

        var day = PickDay(state, today);
        Root.Children.Add(Widgets.SectionHeader("Une journée à redécouvrir", "Changer", Surprise));
        if (day is not null)
        {
            Root.Children.Add(DayCard(day.Value.Day, day.Value.Cards));
        }
        else
        {
            Root.Children.Add(Widgets.EmptyState(
                "Pas encore de journée à proposer",
                "Les souvenirs sans date ne reçoivent pas de fausse journée anniversaire.",
                null, null));
        }

        if (state.Albums.Count > 0)
        {
            Root.Children.Add(Widgets.SectionHeader("Vos albums", "Tout voir", () => App.MainWindow?.Navigate("albums")));
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(0, 0, 0, 24)
            };
            foreach (var album in state.Albums)
            {
                row.Children.Add(AlbumCard(album));
            }
            Root.Children.Add(row);
        }
    }

    private void OpenLibrary(DateOnly day)
    {
        App.MainWindow?.Navigate("library");
    }

    private void Surprise()
    {
        var state = AppState.Current;
        var days = state.Cards
            .Where(c => c.LocalDate is not null)
            .GroupBy(c => c.LocalDate!.Value)
            .OrderBy(_ => Random.Shared.Next())
            .FirstOrDefault();
        if (days is null)
        {
            return;
        }
        var items = days.OrderBy(c => c.LocalDate).ToList();
        App.MainWindow?.ShowStory(items, 0);
    }

    private static (DateOnly Day, List<MemoryCard> Cards)? PickDay(AppState state, DateOnly today)
    {
        var candidates = state.Cards
            .Where(c => c.LocalDate is not null && c.LocalDate < today)
            .GroupBy(c => c.LocalDate!.Value)
            .Where(g => g.Count() >= 1)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => Random.Shared.Next())
            .Take(4)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }
        var pick = candidates[Random.Shared.Next(candidates.Count)];
        return (pick.Key, pick.OrderBy(c => c.Id).ToList());
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
        button.Click += (_, _) => App.MainWindow?.Navigate("albums");
        return button;
    }
}
