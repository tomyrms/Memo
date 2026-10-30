using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Memo.App.Pages;

public sealed partial class AlbumsPage : Page
{
    private List<MemoryCard> _items = new();
    private long _albumId;
    // Empêche un rechargement de la sélection de rappeler ShowAlbumAsync deux fois.
    private bool _suspendSelection;
    private bool _selectionMode;
    // Évite qu'un chargement lent remplace l'album choisi ensuite.
    private int _showVersion;

    public AlbumsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AlbumList.ItemsSource = AppState.Current.Albums;

        // La page est mise en cache : reprendre l'album courant s'il existe
        // encore, sinon le premier album de la collection rechargée.
        var id = _albumId;
        if (id == 0 || AppState.Current.Albums.All(album => album.Id != id))
        {
            id = AppState.Current.Albums.FirstOrDefault()?.Id ?? 0;
        }

        if (id != 0)
        {
            await SelectAlbumAsync(id);
        }
        else
        {
            await ShowAlbumAsync(null);
        }
    }

    private async void OnAlbumSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendSelection)
        {
            return;
        }
        await ShowAlbumAsync(AlbumList.SelectedItem as CollectionInfo);
    }

    /// <summary>
    /// Sélectionne un album par identifiant : après un renommage ou un
    /// réordonnancement, AppState.Albums contient de nouveaux objets et la
    /// sélection de la ListView doit suivre l'identifiant, pas l'instance.
    /// </summary>
    private async Task SelectAlbumAsync(long albumId)
    {
        var album = AppState.Current.Albums.FirstOrDefault(candidate => candidate.Id == albumId);
        if (album is null)
        {
            await ShowAlbumAsync(null);
            return;
        }

        _suspendSelection = true;
        if (!ReferenceEquals(AlbumList.SelectedItem, album))
        {
            AlbumList.SelectedItem = album;
        }
        _suspendSelection = false;
        await ShowAlbumAsync(album);
    }

    private Task ReloadCurrentAlbumAsync()
        => _albumId != 0 ? SelectAlbumAsync(_albumId) : ShowAlbumAsync(null);

    private async Task ShowAlbumAsync(CollectionInfo? album)
    {
        var version = ++_showVersion;
        if (album is null)
        {
            _albumId = 0;
            _items = new List<MemoryCard>();
            ExitSelectionMode();
            AlbumTitle.Text = AppState.Current.Albums.Count == 0 ? string.Empty : "Aucun album sélectionné";
            AlbumMeta.Text = string.Empty;
            AlbumOrderHint.Visibility = Visibility.Collapsed;
            Grid.ItemsSource = null;
            Empty.Visibility = Visibility.Collapsed;
            NoAlbums.Visibility = AppState.Current.Albums.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AlbumActions.Visibility = Visibility.Collapsed;
            var empty = AppState.Current.Albums.Count == 0;
            AlbumList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(AlbumContent, empty ? 0 : 1);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(AlbumContent, empty ? 2 : 1);
            return;
        }

        if (_albumId != album.Id)
        {
            // Changer d'album quitte le mode sélection : les actions portent
            // toujours sur l'album affiché.
            ExitSelectionMode();
        }
        _albumId = album.Id;

        NoAlbums.Visibility = Visibility.Collapsed;
        AlbumActions.Visibility = Visibility.Visible;
        AlbumList.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(AlbumContent, 1);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(AlbumContent, 1);

        var items = (await AppState.Current.GetAlbumItemsAsync(album.Id)).ToList();
        if (version != _showVersion)
        {
            return;
        }
        _items = items;
        AlbumTitle.Text = album.Name;
        var dates = _items
            .Where(card => card.LocalDate is not null)
            .Select(card => card.LocalDate!.Value)
            .ToList();
        var span = dates.Count > 0
            ? $" · du {dates.Min():d MMMM yyyy} au {dates.Max():d MMMM yyyy}"
            : " · sans date";
        AlbumMeta.Text = $"{_items.Count} souvenir(s){span}"
            + (_items.Count > 0 ? " · l’ordre de la grille est celui de la lecture" : string.Empty);
        AlbumOrderHint.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectModeButton.IsEnabled = _items.Count > 0;
        Grid.ItemsSource = _items;
        Grid.SelectedItems.Clear();
        UpdateSelectionUi();
        Empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (_selectionMode || e.ClickedItem is not MemoryCard card)
        {
            return;
        }

        App.MainWindow?.ShowStory(_items, Math.Max(0, _items.IndexOf(card)));
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_items.Count > 0)
        {
            App.MainWindow?.ShowStory(_items, 0);
        }
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_albumId == 0)
        {
            return;
        }

        var existing = _items.Select(card => card.Id).ToHashSet();
        var candidates = AppState.Current.Cards
            .Where(card => !existing.Contains(card.Id))
            .Where(card => card.Status != MemoryStatus.OverlayOnly && card.MainAssetId is not null)
            .OrderBy(card => card.LocalDate ?? DateOnly.MaxValue)
            .ThenBy(card => card.Id)
            .Select(card => new AlbumCandidateRow(card))
            .ToList();

        if (candidates.Count == 0)
        {
            Show(
                "Aucun souvenir à ajouter",
                "Tous les souvenirs de la bibliothèque sont déjà dans cet album, ou aucun média principal n’est disponible.",
                InfoBarSeverity.Informational);
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            Height = 380,
            ItemsSource = candidates,
            ItemTemplate = (DataTemplate)Resources["AlbumCandidateTemplate"]
        };
        var content = new StackPanel { Spacing = 8, Width = 520 };
        content.Children.Add(Widgets.Caption(
            "Sélectionnez un ou plusieurs souvenirs. Ils seront ajoutés à la fin de l’album ; l’ordre reste modifiable dans la grille."));
        content.Children.Add(list);

        var dialog = new ContentDialog
        {
            Title = "Ajouter des souvenirs",
            Content = content,
            PrimaryButtonText = "Ajouter",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var additions = list.SelectedItems
            .OfType<AlbumCandidateRow>()
            .Select(row => row.Card.Id)
            .ToList();
        if (additions.Count == 0)
        {
            return;
        }

        try
        {
            await AppState.Current.AddToAlbumAsync(_albumId, additions);
            await ReloadCurrentAlbumAsync();
            Show(
                "Souvenirs ajoutés",
                $"{additions.Count} souvenir(s) ajouté(s) à la fin de l’album. Aucun fichier source n’a été déplacé.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Ajout impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = await AskNameAsync("Nouvel album", string.Empty, "Créer");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var existing = AppState.Current.Albums.Select(album => album.Id).ToHashSet();
            await AppState.Current.CreateAlbumAsync(name.Trim());
            var created = AppState.Current.Albums.FirstOrDefault(album => !existing.Contains(album.Id));
            if (created is not null)
            {
                await SelectAlbumAsync(created.Id);
            }
        }
        catch (Exception ex)
        {
            Show("Création impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        var album = AppState.Current.Albums.FirstOrDefault(candidate => candidate.Id == _albumId);
        if (album is null)
        {
            return;
        }

        var name = await AskNameAsync("Renommer l'album", album.Name, "Renommer");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            // Conserver l'identifiant : le renommage peut réordonner Albums par nom.
            await AppState.Current.RenameAlbumAsync(album.Id, name.Trim());
            await SelectAlbumAsync(album.Id);
        }
        catch (Exception ex)
        {
            Show("Renommage impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var album = AppState.Current.Albums.FirstOrDefault(candidate => candidate.Id == _albumId);
        if (album is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Supprimer l'album ?",
            Content = $"« {album.Name} » sera retiré. Les fichiers sources et les souvenirs restent dans la bibliothèque.",
            PrimaryButtonText = "Supprimer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await AppState.Current.DeleteAlbumAsync(album.Id);
            var next = AppState.Current.Albums.FirstOrDefault();
            if (next is not null)
            {
                await SelectAlbumAsync(next.Id);
            }
            else
            {
                await ShowAlbumAsync(null);
            }
            Show(
                "Album supprimé",
                "Les souvenirs et leurs fichiers sources restent dans la bibliothèque.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Suppression impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnToggleSelectionMode(object sender, RoutedEventArgs e)
    {
        if (_selectionMode)
        {
            ExitSelectionMode();
        }
        else
        {
            EnterSelectionMode();
        }
    }

    private void EnterSelectionMode()
    {
        _selectionMode = true;
        Grid.SelectedItems.Clear();
        Grid.SelectionMode = ListViewSelectionMode.Multiple;
        Grid.IsItemClickEnabled = false;
        SelectModeButton.Content = "Terminer";
        SelectionBar.Visibility = Visibility.Visible;
        UpdateSelectionUi();
    }

    private void ExitSelectionMode()
    {
        _selectionMode = false;
        Grid.SelectedItems.Clear();
        Grid.SelectionMode = ListViewSelectionMode.Single;
        Grid.IsItemClickEnabled = true;
        SelectModeButton.Content = "Sélectionner";
        SelectionBar.Visibility = Visibility.Collapsed;
        UpdateSelectionUi();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionUi();

    private void UpdateSelectionUi()
    {
        var count = Grid.SelectedItems.Count;
        SelectionCount.Text = count == 0
            ? "Aucun souvenir sélectionné"
            : $"{count} sélectionné(s)";
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        Grid.SelectedItems.Clear();
        UpdateSelectionUi();
    }

    private async void OnRemoveSelected(object sender, RoutedEventArgs e)
        => await RemoveSelectedAsync();

    private async Task RemoveSelectedAsync()
    {
        var ids = Grid.SelectedItems
            .OfType<MemoryCard>()
            .Select(card => card.Id)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        await RemoveCardsAsync(ids, confirm: ids.Count > 1);
    }

    private async Task RemoveCardsAsync(IReadOnlyList<long> memoryIds, bool confirm)
    {
        if (_albumId == 0 || memoryIds.Count == 0)
        {
            return;
        }

        if (confirm)
        {
            var dialog = new ContentDialog
            {
                Title = "Retirer de l’album ?",
                Content = $"{memoryIds.Count} souvenir(s) seront retirés de cet album. " +
                          "Ils restent dans la bibliothèque, avec leurs dates et leurs fichiers sources.",
                PrimaryButtonText = "Retirer",
                CloseButtonText = "Annuler",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        try
        {
            await AppState.Current.RemoveFromAlbumAsync(_albumId, memoryIds);
            ExitSelectionMode();
            await ReloadCurrentAlbumAsync();
            Show(
                "Souvenirs retirés",
                $"{memoryIds.Count} souvenir(s) retiré(s) de l’album. Les souvenirs et les fichiers sources restent intacts dans la bibliothèque.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Retrait impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnMoveUpAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Grid.SelectedItem is MemoryCard card)
        {
            args.Handled = true;
            await MoveWithFeedbackAsync(card.Id, -1);
        }
    }

    private async void OnMoveDownAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Grid.SelectedItem is MemoryCard card)
        {
            args.Handled = true;
            await MoveWithFeedbackAsync(card.Id, 1);
        }
    }

    private async void OnRemoveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Grid.SelectedItems.Count == 0)
        {
            return;
        }

        args.Handled = true;
        await RemoveSelectedAsync();
    }

    private async Task MoveWithFeedbackAsync(long memoryId, int delta)
    {
        try
        {
            await MoveAsync(memoryId, delta);
        }
        catch (Exception ex)
        {
            Show("Réordonnancement impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Échange un souvenir avec son voisin et enregistre l'ordre complet.
    /// Les fichiers sources ne sont jamais touchés : seule la position dans
    /// la collection change.
    /// </summary>
    private async Task<bool> MoveAsync(long memoryId, int delta)
    {
        var index = _items.FindIndex(card => card.Id == memoryId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _items.Count)
        {
            return false;
        }

        var selectedIds = Grid.SelectedItems
            .OfType<MemoryCard>()
            .Select(card => card.Id)
            .ToHashSet();
        var ordered = _items.Select(card => card.Id).ToList();
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);

        await AppState.Current.ReorderAlbumAsync(_albumId, ordered);
        await ReloadCurrentAlbumAsync();

        if (_selectionMode)
        {
            foreach (var card in _items.Where(card => selectedIds.Contains(card.Id)))
            {
                Grid.SelectedItems.Add(card);
            }
        }
        else
        {
            Grid.SelectedItem = _items.FirstOrDefault(card => card.Id == memoryId);
        }

        return true;
    }

    /// <summary>
    /// Menu contextuel d'une tuile : lecture à partir d'ici, déplacement
    /// clavier/souris et retrait sans suppression du souvenir.
    /// </summary>
    private void OnTileContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_selectionMode || sender is not FrameworkElement element ||
            element.DataContext is not MemoryCard card)
        {
            return;
        }

        var index = _items.FindIndex(candidate => candidate.Id == card.Id);
        if (index < 0)
        {
            return;
        }

        var flyout = new MenuFlyout();

        var play = new MenuFlyoutItem { Text = "Lire à partir d’ici" };
        play.Click += (_, _) => App.MainWindow?.ShowStory(_items, index);
        flyout.Items.Add(play);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var up = new MenuFlyoutItem { Text = "Monter", IsEnabled = index > 0 };
        up.Click += async (_, _) => await MoveWithFeedbackAsync(card.Id, -1);
        flyout.Items.Add(up);

        var down = new MenuFlyoutItem { Text = "Descendre", IsEnabled = index < _items.Count - 1 };
        down.Click += async (_, _) => await MoveWithFeedbackAsync(card.Id, 1);
        flyout.Items.Add(down);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var remove = new MenuFlyoutItem { Text = "Retirer de l’album" };
        remove.Click += async (_, _) => await RemoveCardsAsync(new[] { card.Id }, confirm: false);
        flyout.Items.Add(remove);

        flyout.ShowAt(element);
        args.Handled = true;
    }

    private async Task<string?> AskNameAsync(string title, string initial, string primary)
    {
        var box = new TextBox { Text = initial, PlaceholderText = "Nom de l'album", MaxLength = 80 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    private void OnGoLibrary(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("library");

    private void Show(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    /// <summary>Ligne compacte de la sélection multiple à l'ajout.</summary>
    private sealed record AlbumCandidateRow(MemoryCard Card)
    {
        public string DateLabel => Card.LocalDate is { } date
            ? date.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy", AppState.French)
            : "Date inconnue";

        public string DetailLabel
        {
            get
            {
                var parts = new List<string>
                {
                    Card.Kind == MediaKind.Video ? "Vidéo" : Card.Kind == MediaKind.Photo ? "Photo" : "Calque"
                };
                if (!string.IsNullOrWhiteSpace(Card.LocationLabel))
                {
                    parts.Add(Card.LocationLabel);
                }
                if (Card.IsFavorite)
                {
                    parts.Add("Favori");
                }
                if (Card.IsExcluded)
                {
                    parts.Add("Masqué");
                }
                return string.Join(" · ", parts);
            }
        }
    }
}
