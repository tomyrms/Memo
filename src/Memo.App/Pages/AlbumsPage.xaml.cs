using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Memo.App.Pages;

public sealed partial class AlbumsPage : Page
{
    private List<MemoryCard> _items = new();

    public AlbumsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            AlbumList.ItemsSource = AppState.Current.Albums;
            if (AlbumList.SelectedIndex < 0 && AppState.Current.Albums.Count > 0)
            {
                AlbumList.SelectedIndex = 0;
            }
            await ShowAlbumAsync(AlbumList.SelectedItem as CollectionInfo);
        };
    }

    private async void OnAlbumSelected(object sender, SelectionChangedEventArgs e)
        => await ShowAlbumAsync(AlbumList.SelectedItem as CollectionInfo);

    private async Task ShowAlbumAsync(CollectionInfo? album)
    {
        if (album is null)
        {
            AlbumTitle.Text = AppState.Current.Albums.Count == 0 ? string.Empty : "Aucun album sélectionné";
            AlbumMeta.Text = string.Empty;
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

        NoAlbums.Visibility = Visibility.Collapsed;
        AlbumActions.Visibility = Visibility.Visible;
        AlbumList.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(AlbumContent, 1);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(AlbumContent, 1);
        _items = (await AppState.Current.GetAlbumItemsAsync(album.Id)).ToList();
        AlbumTitle.Text = album.Name;
        AlbumMeta.Text = $"{_items.Count} souvenir(s)" + (album.FirstDate is { } first ? $" · depuis le {first:d MMMM yyyy}" : " · sans date");
        Grid.ItemsSource = _items;
        Empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MemoryCard card)
        {
            App.MainWindow?.ShowStory(_items, Math.Max(0, _items.IndexOf(card)));
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_items.Count > 0)
        {
            App.MainWindow?.ShowStory(_items, 0);
        }
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = await AskNameAsync("Nouvel album", string.Empty, "Créer");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var id = AppState.Current.Albums.Count == 0 ? 1 : AppState.Current.Albums.Max(a => a.Id) + 1;
        await AppState.Current.CreateAlbumAsync(name.Trim());
        AlbumList.SelectedItem = AppState.Current.Albums.Count > 0 ? AppState.Current.Albums[^1] : null;
        await ShowAlbumAsync(AlbumList.SelectedItem as CollectionInfo);
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (AlbumList.SelectedItem is not CollectionInfo album)
        {
            return;
        }
        var name = await AskNameAsync("Renommer l'album", album.Name, "Renommer");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        await AppState.Current.RenameAlbumAsync(album.Id, name.Trim());
        await ShowAlbumAsync(AlbumList.SelectedItem as CollectionInfo);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (AlbumList.SelectedItem is not CollectionInfo album)
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
        await AppState.Current.DeleteAlbumAsync(album.Id);
        AlbumList.SelectedIndex = AppState.Current.Albums.Count > 0 ? 0 : -1;
        await ShowAlbumAsync(AlbumList.SelectedItem as CollectionInfo);
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
}

