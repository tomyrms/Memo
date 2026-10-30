using Memo.App.Controls;
using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Memo.App.Pages;

public sealed partial class MapPage : Page
{
    private List<MemoryCard> _located = new();

    public MapPage()
    {
        InitializeComponent();
        Map.PointSelected += OnPointSelected;
        Loaded += async (_, _) => await BuildAsync();
    }

    private async Task BuildAsync()
    {
        _located = AppState.Current.Cards
            .Where(c => c.Latitude is not null && c.Longitude is not null)
            .OrderByDescending(c => c.LocalDate)
            .ToList();

        var points = _located
            .Select(c => new MapPoint(c.Latitude!.Value, c.Longitude!.Value, c.Id, c.LocationLabel ?? "Lieu"))
            .ToList();
        Map.SetPoints(points);

        Places.ItemsSource = _located.Select(c => new PlaceItem(
            c.LocationLabel ?? "Lieu",
            Widgets.DateLabel(c),
            c)).ToList();
        Places.DisplayMemberPath = null;

        CountLabel.Text = _located.Count == 0
            ? "Aucune coordonnée liée à un souvenir."
            : $"{_located.Count} souvenir(s) avec un lieu. Carte hors ligne, sans fond de carte distant.";
        Subtitle.Text = "Seuls les souvenirs avec un lieu explicite apparaissent. L'historique du compte n'est pas utilisé.";
        Empty.Visibility = _located.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Places.Visibility = _located.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        try
        {
            await LoadEventPlacesAsync();
        }
        catch
        {
            // La carte existante reste utilisable même si les suggestions échouent.
            EventPlacesExpander.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Ajoute les lieux explicitement associés aux événements proposés.
    /// Aucune coordonnée n'est déduite : seuls les libellés déjà portés par un
    /// souvenir sont affichés. Les marqueurs de la carte restent inchangés.
    /// </summary>
    private async Task LoadEventPlacesAsync()
    {
        var suggestions = await EventSuggestionService.Current.BuildSuggestionsAsync();
        var withPlaces = suggestions.Where(s => s.HasPlaces).ToList();
        var events = withPlaces.Take(4).ToList();

        EventPlaces.Children.Clear();
        EventPlacesExpander.Visibility = events.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EventPlacesExpander.IsExpanded = events.Count > 0;
        if (events.Count == 0)
        {
            return;
        }

        EventPlaces.Children.Add(Widgets.Caption(
            "Lieux explicitement associés aux souvenirs. Aucune position n'est déduite d'un historique."));

        foreach (var suggestion in events)
        {
            var panel = new StackPanel { Spacing = 1 };
            panel.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", suggestion.Places),
                Style = Widgets.Style("MemoBodyTextStyle"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"{suggestion.PeriodLabel} · {suggestion.MemoryCount} souvenir(s)",
                Style = Widgets.Style("MemoCaptionTextStyle"),
            });

            var button = new Button
            {
                Content = panel,
                Style = Widgets.Style("MemoQuietButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(4, 6, 4, 6),
            };
            button.Click += (_, _) => App.MainWindow?.ShowStory(suggestion.Memories, 0);
            EventPlaces.Children.Add(button);
        }

        if (withPlaces.Count > events.Count)
        {
            EventPlaces.Children.Add(Widgets.Caption(
                $"… et {withPlaces.Count - events.Count} autre(s) événement(s) avec un lieu. " +
                "La liste complète est sur l’écran Événements."));
        }
    }

    private void OnPointSelected(object? sender, MapPoint point)
    {
        var card = _located.FirstOrDefault(c => c.Id == point.MemoryId);
        if (card is not null)
        {
            _ = ShowCardAsync(card);
        }
    }

    private void OnPlaceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Places.SelectedItem is PlaceItem item)
        {
            _ = ShowCardAsync(item.Card);
            Places.SelectedItem = null;
        }
    }

    private async Task ShowCardAsync(MemoryCard card)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = Widgets.DateLabel(card), Style = Widgets.Style("MemoSubtitleTextStyle") });
        panel.Children.Add(Widgets.Secondary(card.LocationLabel ?? "Lieu sans nom"));
        panel.Children.Add(Widgets.Caption("Coordonnées liées au souvenir dans cette démonstration."));

        var dialog = new ContentDialog
        {
            Title = "Souvenir situé",
            Content = panel,
            PrimaryButtonText = "Lire",
            CloseButtonText = "Fermer",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            App.MainWindow?.ShowStory(new[] { card }, 0);
        }
    }

    private void OnGoLibrary(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("library");

    public sealed record PlaceItem(string Label, string Date, MemoryCard Card);
}

