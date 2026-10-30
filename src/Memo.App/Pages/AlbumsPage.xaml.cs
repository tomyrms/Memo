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
        Loaded += (_, _) =>
        {
            AlbumList.ItemsSource = AppState.Current.Albums;
            if (AlbumList.SelectedIndex < 0 && AppState.Current.Albums.Count > 0)
            {
                AlbumList.SelectedIndex = 0;
            }
            ShowAlbum(AlbumList.SelectedItem as CollectionInfo);
        };
    }

    private void OnAlbumSelected(object sender, SelectionChangedEventArgs e)
        => ShowAlbum(AlbumList.SelectedItem as CollectionInfo);

    private void ShowAlbum(CollectionInfo? album)
    {
        if (album is null)
        {
            AlbumTitle.Text = "Aucun album sélectionné";
            AlbumMeta.Text = string.Empty;
            Grid.ItemsSource = null;
            Empty.Visibility = Visibility.Collapsed;
            return;
        }

        var ids = DemoData.AlbumMembers(album.Id);
        _items = AppState.Current.Cards.Where(c => ids.Contains(c.Id)).ToList();
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
        AppState.Current.Albums.Add(new CollectionInfo(id, name.Trim(), CollectionKind.Album, DateTimeOffset.Now, 0, null, null, null));
        AlbumList.SelectedItem = AppState.Current.Albums[^1];
        ShowAlbum(AlbumList.SelectedItem as CollectionInfo);
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
        var index = AppState.Current.Albums.IndexOf(album);
        if (index >= 0)
        {
            AppState.Current.Albums[index] = album with { Name = name.Trim() };
            AlbumList.SelectedIndex = index;
            ShowAlbum(AppState.Current.Albums[index]);
        }
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
        AppState.Current.Albums.Remove(album);
        AlbumList.SelectedIndex = AppState.Current.Albums.Count > 0 ? 0 : -1;
        ShowAlbum(AlbumList.SelectedItem as CollectionInfo);
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
