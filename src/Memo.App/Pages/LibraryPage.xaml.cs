using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Memo.App.Pages;

public sealed partial class LibraryPage : Page
{
    private bool _selectionMode;
    private bool _ready;
    private List<MemoryCard> _filtered = new();

    public LibraryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { _ready = true; BuildYears(); Refresh(); };
        Grid.SelectionChanged += (_, _) => UpdateSelectionBar();

        var find = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.F,
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        find.Invoked += (_, args) =>
        {
            SearchBox.Focus(FocusState.Programmatic);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(find);
    }

    private void BuildYears()
    {
        YearFilter.Items.Clear();
        YearFilter.Items.Add(new ComboBoxItem { Content = "Toutes les années", Tag = 0, IsSelected = true });
        foreach (var year in AppState.Current.Cards
                     .Where(c => c.LocalDate is not null)
                     .Select(c => c.LocalDate!.Value.Year)
                     .Distinct()
                     .OrderByDescending(y => y))
        {
            YearFilter.Items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year });
        }
    }

    private void Refresh()
    {
        if (!_ready)
        {
            return;
        }
        var state = AppState.Current;
        IEnumerable<MemoryCard> query = state.Cards;

        if (UndatedToggle.IsChecked == true)
        {
            query = query.Where(c => c.LocalDate is null);
        }
        if (MissingToggle.IsChecked == true)
        {
            query = query.Where(c => c.Status == MemoryStatus.OverlayOnly);
        }
        if (TypeFilter.SelectedItem is ComboBoxItem type && type.Tag is string tag && tag != "all")
        {
            query = tag == "video"
                ? query.Where(c => c.Kind == MediaKind.Video)
                : query.Where(c => c.Kind != MediaKind.Video);
        }
        if (YearFilter.SelectedItem is ComboBoxItem yearItem && yearItem.Tag is int year && year > 0)
        {
            query = query.Where(c => c.LocalDate?.Year == year);
        }

        var search = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(c =>
                (c.LocationLabel?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                c.ExternalKey.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (c.LocalDate?.ToString("dddd d MMMM yyyy", AppState.French).Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        _filtered = query
            .OrderByDescending(c => c.LocalDate is not null)
            .ThenByDescending(c => c.LocalDate)
            .ThenBy(c => c.ExternalKey)
            .ToList();

        var groups = _filtered
            .Where(c => c.LocalDate is not null)
            .GroupBy(c => new DateTime(c.LocalDate!.Value.Year, c.LocalDate.Value.Month, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => new MemoryGroup(FormatMonth(g.Key), g.OrderBy(c => c.LocalDate)))
            .ToList();

        var undated = _filtered.Where(c => c.LocalDate is null).ToList();
        if (undated.Count > 0)
        {
            groups.Add(new MemoryGroup("Date inconnue", undated));
        }

        var cvs = new CollectionViewSource { IsSourceGrouped = true, Source = groups };
        Grid.ItemsSource = cvs.View;

        CountLabel.Text = _filtered.Count == state.Cards.Count
            ? $"{_filtered.Count} souvenir(s)"
            : $"{_filtered.Count} sur {state.Cards.Count} souvenirs";
        EmptyPanel.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionBar();
    }

    private static string FormatMonth(DateTime date)
    {
        var text = date.ToString("MMMM yyyy", AppState.French);
        return char.ToUpper(text[0], AppState.French) + text[1..];
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            Refresh();
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        TypeFilter.SelectedIndex = 0;
        YearFilter.SelectedIndex = 0;
        UndatedToggle.IsChecked = false;
        MissingToggle.IsChecked = false;
        Refresh();
    }

    private void OnToggleSelection(object sender, RoutedEventArgs e)
    {
        _selectionMode = !_selectionMode;
        Grid.SelectionMode = _selectionMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        Grid.IsItemClickEnabled = !_selectionMode;
        SelectButton.Content = _selectionMode ? "Terminer" : "Sélectionner";
        SelectionBar.Visibility = _selectionMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_selectionMode)
        {
            Grid.SelectedItems.Clear();
        }
        UpdateSelectionBar();
    }

    private void UpdateSelectionBar()
    {
        var count = Grid.SelectedItems.Count;
        SelectionLabel.Text = count == 0 ? "Aucun souvenir sélectionné" : $"{count} souvenir(s) sélectionné(s)";
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MemoryCard card)
        {
            return;
        }
        var index = _filtered.IndexOf(card);
        if (index < 0)
        {
            index = 0;
        }
        App.MainWindow?.ShowStory(_filtered, index);
    }

    private void OnGridRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var card = FindCard(e.OriginalSource as DependencyObject);
        if (card is null)
        {
            return;
        }

        var flyout = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Ouvrir en Story" };
        open.Click += (_, _) => App.MainWindow?.ShowStory(_filtered, Math.Max(0, _filtered.IndexOf(card)));
        flyout.Items.Add(open);

        var info = new MenuFlyoutItem { Text = "Informations et date" };
        info.Click += async (_, _) => await ShowInfoAsync(card);
        flyout.Items.Add(info);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var favorite = new MenuFlyoutItem { Text = card.IsFavorite ? "Retirer des favoris" : "Ajouter aux favoris" };
        favorite.Click += async (_, _) => await ToggleFavoriteAsync(new[] { card });
        flyout.Items.Add(favorite);

        var exclude = new MenuFlyoutItem { Text = card.IsExcluded ? "Réintégrer aux suggestions" : "Masquer des suggestions" };
        exclude.Click += async (_, _) => await ToggleExcludedAsync(new[] { card });
        flyout.Items.Add(exclude);

        flyout.ShowAt(Grid, new FlyoutShowOptions { Position = e.GetPosition(Grid) });
        e.Handled = true;
    }

    private static MemoryCard? FindCard(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: MemoryCard card })
            {
                return card;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void OnFavoriteSelection(object sender, RoutedEventArgs e)
        => _ = ToggleFavoriteAsync(Selected());

    private void OnExcludeSelection(object sender, RoutedEventArgs e)
        => _ = ToggleExcludedAsync(Selected());

    private List<MemoryCard> Selected()
        => Grid.SelectedItems.OfType<MemoryCard>().ToList();

    private async Task ToggleFavoriteAsync(IReadOnlyList<MemoryCard> cards)
    {
        foreach (var card in cards)
        {
            await AppState.Current.SetFavoriteAsync(card.Id, !card.IsFavorite);
        }
        Refresh();
    }

    private async Task ToggleExcludedAsync(IReadOnlyList<MemoryCard> cards)
    {
        foreach (var card in cards)
        {
            await AppState.Current.SetExcludedAsync(card.Id, !card.IsExcluded);
        }
        Refresh();
    }

    private static void Replace(MemoryCard updated)
    {
        var list = AppState.Current.Cards;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == updated.Id)
            {
                list[i] = updated;
                return;
            }
        }
    }

    private async void OnAddToAlbum(object sender, RoutedEventArgs e)
    {
        var cards = Selected();
        if (cards.Count == 0)
        {
            return;
        }
        await AddToAlbumAsync(cards);
    }

    private async Task AddToAlbumAsync(IReadOnlyList<MemoryCard> cards)
    {
        var combo = new ComboBox { MinWidth = 260, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var album in AppState.Current.Albums)
        {
            combo.Items.Add(new ComboBoxItem { Content = album.Name, Tag = album.Id, IsSelected = combo.Items.Count == 0 });
        }
        if (combo.Items.Count == 0)
        {
            var empty = new ContentDialog
            {
                Title = "Aucun album",
                Content = "Créez d'abord un album depuis la page Albums.",
                CloseButtonText = "Fermer",
                XamlRoot = XamlRoot
            };
            await empty.ShowAsync();
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Ajouter à un album",
            Content = combo,
            PrimaryButtonText = "Ajouter",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is long albumId)
        {
            await AppState.Current.AddToAlbumAsync(albumId, cards.Select(c => c.Id).ToList());
        }
    }

    private async Task ShowInfoAsync(MemoryCard card)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = Widgets.DateLabel(card),
            Style = Widgets.Style("MemoSubtitleTextStyle")
        });

        void Line(string label, string value)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, Style = Widgets.Style("MemoSecondaryTextStyle") });
            var val = new TextBlock { Text = value, Style = Widgets.Style("MemoBodyTextStyle"), TextWrapping = TextWrapping.Wrap };
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(val, 1);
            row.Children.Add(val);
            panel.Children.Add(row);
        }

        Line("Type", card.Kind == MediaKind.Video ? "Vidéo" : card.Kind == MediaKind.Photo ? "Photo" : "Calque");
        Line("Statut", card.Status switch
        {
            MemoryStatus.Complete => "Média et calque",
            MemoryStatus.MediaOnly => "Média seul (aucun calque importé)",
            MemoryStatus.OverlayOnly => "Calque seul — média non trouvé dans les archives importées",
            MemoryStatus.Conflict => "Variantes en conflit",
            _ => "Inconnu"
        });
        Line("Dimensions", card.Width is > 0 ? $"{card.Width} × {card.Height}" : "Non disponibles");
        Line("Durée", card.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss") : "—");
        Line("Fichier", $"{card.Format?.ToUpperInvariant()} · {FormatSize(card.SizeBytes)}");
        Line("Date source", card.DateSource switch
        {
            DateSource.Mp4CreationTime => "creation_time du conteneur MP4 (UTC)",
            DateSource.ExifDateTimeOriginal => "EXIF DateTimeOriginal",
            DateSource.ExifDateTime => "EXIF DateTime",
            DateSource.User => "Correction manuelle",
            _ => "Aucune date"
        });
        Line("Provenance", AppState.Current.IsDemo
            ? "Jeu de démonstration synthétique"
            : AppState.Current.LibraryPath ?? "Bibliothèque locale");
        if (!string.IsNullOrWhiteSpace(card.LocationLabel))
        {
            Line("Lieu", card.LocationLabel);
        }

        var picker = new CalendarDatePicker
        {
            Header = "Corriger la date de classement",
            Date = card.LocalDate?.ToDateTime(TimeOnly.MinValue),
            PlaceholderText = "Choisir une date",
            Margin = new Thickness(0, 12, 0, 0)
        };
        panel.Children.Add(picker);

        var locationLabel = new TextBox
        {
            Header = "Lieu",
            Text = card.LocationLabel ?? string.Empty,
            PlaceholderText = "Ex. Lausanne",
            Margin = new Thickness(0, 8, 0, 0)
        };
        var latitude = new NumberBox
        {
            Header = "Latitude",
            Value = card.Latitude ?? double.NaN,
            Minimum = -90,
            Maximum = 90,
            SmallChange = 0.0001,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var longitude = new NumberBox
        {
            Header = "Longitude",
            Value = card.Longitude ?? double.NaN,
            Minimum = -180,
            Maximum = 180,
            SmallChange = 0.0001,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden
        };
        var coords = new Grid { ColumnSpacing = 8 };
        coords.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        coords.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        coords.Children.Add(latitude);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(longitude, 1);
        coords.Children.Add(longitude);
        panel.Children.Add(locationLabel);
        panel.Children.Add(coords);
        panel.Children.Add(Widgets.Caption("La date d'origine reste conservée. Le lieu n'est jamais déduit de l'historique du compte."));

        var dialog = new ContentDialog
        {
            Title = "Informations du souvenir",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "Enregistrer",
            CloseButtonText = "Fermer",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var chosenDate = picker.Date is { } chosen
                ? DateOnly.FromDateTime(chosen.Date)
                : (DateOnly?)null;
            if (chosenDate != card.LocalDate)
            {
                await AppState.Current.SetDateOverrideAsync(card.Id, chosenDate);
            }

            double? lat = double.IsNaN(latitude.Value) ? null : latitude.Value;
            double? lon = double.IsNaN(longitude.Value) ? null : longitude.Value;
            if (lat is null || lon is null)
            {
                lat = null;
                lon = null;
            }
            var label = string.IsNullOrWhiteSpace(locationLabel.Text) ? null : locationLabel.Text.Trim();
            await AppState.Current.SetLocationAsync(card.Id, lat, lon, label);
            Refresh();
        }
    }

    private static string FormatSize(long bytes)
        => bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.#} Mo" : $"{bytes / 1000.0:0} Ko";

    private void OnImport(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("imports");

    public sealed class MemoryGroup : List<MemoryCard>
    {
        public MemoryGroup(string key, IEnumerable<MemoryCard> items) : base(items)
        {
            Key = key;
        }

        public string Key { get; }
    }
}










