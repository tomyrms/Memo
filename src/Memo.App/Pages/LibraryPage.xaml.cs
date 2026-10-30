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
    private const int SearchDelayMs = 275;

    private bool _selectionMode;
    private bool _ready;
    private List<MemoryCard> _filtered = new();
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;

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

    /// <summary>Critères de filtrage capturés depuis l'interface, réutilisables hors du fil UI.</summary>
    private readonly record struct FilterCriteria(
        bool UndatedOnly,
        bool MissingOnly,
        string TypeTag,
        int Year,
        string Search);

    private FilterCriteria CaptureCriteria() => new(
        UndatedToggle.IsChecked == true,
        MissingToggle.IsChecked == true,
        (TypeFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all",
        YearFilter.SelectedItem is ComboBoxItem { Tag: int year } ? year : 0,
        SearchBox.Text?.Trim() ?? string.Empty);

    private static List<MemoryCard> ApplyFilter(IEnumerable<MemoryCard> cards, FilterCriteria criteria)
    {
        IEnumerable<MemoryCard> query = cards;

        if (criteria.UndatedOnly)
        {
            query = query.Where(c => c.LocalDate is null);
        }
        if (criteria.MissingOnly)
        {
            query = query.Where(c => c.Status == MemoryStatus.OverlayOnly);
        }
        if (criteria.TypeTag == "video")
        {
            query = query.Where(c => c.Kind == MediaKind.Video);
        }
        else if (criteria.TypeTag != "all")
        {
            query = query.Where(c => c.Kind != MediaKind.Video);
        }
        if (criteria.Year > 0)
        {
            query = query.Where(c => c.LocalDate?.Year == criteria.Year);
        }
        if (criteria.Search.Length > 0)
        {
            var search = criteria.Search;
            query = query.Where(c =>
                (c.LocationLabel?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                c.ExternalKey.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (c.LocalDate?.ToString("dddd d MMMM yyyy", AppState.French).Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return query
            .OrderByDescending(c => c.LocalDate is not null)
            .ThenByDescending(c => c.LocalDate)
            .ThenBy(c => c.ExternalKey)
            .ToList();
    }

    private void Refresh()
    {
        if (!_ready)
        {
            return;
        }

        // Un changement de filtre remplace immédiatement toute frappe encore en attente.
        CancelPendingSearch();
        ApplyFiltered(ApplyFilter(AppState.Current.Cards, CaptureCriteria()));
    }

    private void ApplyFiltered(IReadOnlyList<MemoryCard> filtered)
    {
        _filtered = filtered.ToList();

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

        var total = AppState.Current.Cards.Count;
        CountLabel.Text = _filtered.Count == total
            ? $"{_filtered.Count} souvenir(s)"
            : $"{_filtered.Count} sur {total} souvenirs";
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
            ScheduleSearch();
        }
    }

    /// <summary>
    /// Attend une courte pause avant de filtrer. Chaque frappe annule la passe précédente, et le
    /// numéro de génération empêche un résultat obsolète d'écraser un texte plus récent.
    /// </summary>
    private void ScheduleSearch()
    {
        CancelPendingSearch();
        var generation = ++_searchGeneration;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = DebouncedSearchAsync(generation, cts);
    }

    private void CancelPendingSearch()
    {
        // La tâche propriétaire du jeton le libère dans son finally.
        _searchCts?.Cancel();
        _searchCts = null;
    }

    private async Task DebouncedSearchAsync(int generation, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SearchDelayMs, cts.Token);

            // Les contrôles sont lus sur le fil UI, puis le filtrage s'exécute en arrière-plan.
            var criteria = CaptureCriteria();
            var snapshot = AppState.Current.Cards.ToList();
            var filtered = await Task.Run(() => ApplyFilter(snapshot, criteria), cts.Token);

            cts.Token.ThrowIfCancellationRequested();
            if (generation != _searchGeneration)
            {
                return;
            }
            ApplyFiltered(filtered);
        }
        catch (OperationCanceledException)
        {
            // Frappe remplacée : aucun résultat intermédiaire n'est affiché.
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
            }
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

        // Le texte OCR reste une donnée d'analyse en lecture seule : il est affiché ici sans
        // jamais remplacer ni compléter la légende d'origine du souvenir.
        if (AppState.Current.Library is not null)
        {
            string ocrMessage;
            try
            {
                var ocrText = await AppState.Current.Library.Analysis.GetOcrTextAsync(card.Id);
                ocrMessage = string.IsNullOrWhiteSpace(ocrText)
                    ? "Aucun texte reconnu pour ce souvenir."
                    : ocrText;
            }
            catch (Exception)
            {
                // Une bibliothèque fermée pendant l'ouverture de la fiche ne doit pas interrompre l'application.
                ocrMessage = "Texte OCR indisponible pour le moment.";
            }

            panel.Children.Add(new TextBlock
            {
                Text = "Texte reconnu (OCR)",
                Style = Widgets.Style("MemoSecondaryTextStyle"),
                Margin = new Thickness(0, 8, 0, 0)
            });
            panel.Children.Add(new Border
            {
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources["MemoSurfaceAltBrush"],
                BorderBrush = (Brush)Application.Current.Resources["MemoBorderBrush"],
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = ocrMessage,
                    Style = Widgets.Style("MemoBodyTextStyle"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            });
            panel.Children.Add(Widgets.Caption(
                "Texte issu de l'OCR local, jamais de la légende d'origine. Il peut être supprimé depuis Réglages > Données d'analyse."));
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










