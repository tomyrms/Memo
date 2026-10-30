using Memo.App.Pages;
using Memo.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Memo.App;

public sealed partial class ShellPage : UserControl
{
    private string _current = "home";
    // Vrai tant que l'échec d'ouverture de la dernière bibliothèque attend un
    // choix explicite de l'utilisateur.
    private bool _recoveryActive;

    public string? StartPage { get; set; }

    public event EventHandler? Ready;

    public ShellPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var last = AppState.ReadLastLibrary();
        if (!string.IsNullOrEmpty(last))
        {
            string? failure = null;
            if (!Directory.Exists(last))
            {
                failure = $"Le dossier « {last} » est introuvable : il a peut-être été déplacé, renommé ou supprimé.";
            }
            else
            {
                try
                {
                    await AppState.Current.OpenLibraryAsync(last);
                }
                catch (Exception ex)
                {
                    failure = $"L'ouverture de « {last} » a échoué : {ex.Message}";
                }
            }

            if (failure is not null)
            {
                // Ne jamais basculer silencieusement en démonstration : l'échec
                // reste affiché avec les deux suites possibles.
                AppState.Current.LoadDemo();
                ShowRecovery(failure);
            }
        }
        else
        {
            AppState.Current.LoadDemo();
        }

        AppState.Current.LibraryChanged += (_, _) => UpdateChrome();
        UpdateChrome();
        var target = string.IsNullOrWhiteSpace(StartPage) ? "home" : StartPage;
        Select(target);
        Navigate(target);
        Ready?.Invoke(this, EventArgs.Empty);
    }

    private void ShowRecovery(string reason)
    {
        _recoveryActive = true;
        RecoveryMessage.Text = reason +
            " Memo affiche la bibliothèque de démonstration pour cette session ; vos fichiers n'ont pas été modifiés.";
        RecoveryBar.IsOpen = true;
    }

    private void UpdateChrome()
    {
        // Une bibliothèque ouverte entre-temps (depuis Imports ou Réglages)
        // rend le message d'échec obsolète.
        if (AppState.Current.Library is not null && _recoveryActive)
        {
            _recoveryActive = false;
            RecoveryBar.IsOpen = false;
        }

        LibraryLabel.Text = AppState.Current.LibraryName;
        StorageLabel.Text = $"{AppState.Current.Cards.Count} souvenirs";
        DemoBar.IsOpen = AppState.Current.IsDemo && !_recoveryActive;
        DemoBadge.Visibility = AppState.Current.IsDemo ? Visibility.Visible : Visibility.Collapsed;
        App.MainWindow?.ApplyTheme(AppState.Current.Settings.Theme);
    }

    /// <summary>
    /// Ouvre une autre bibliothèque sans quitter le message d'échec : si
    /// l'ouverture échoue, les deux choix restent disponibles.
    /// </summary>
    private async void OnOpenAnotherLibrary(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        try
        {
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        }
        catch (Exception ex)
        {
            RecoveryMessage.Text = "Le sélecteur de dossier n'a pas pu s'ouvrir : " + ex.Message;
            return;
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        try
        {
            await AppState.Current.OpenLibraryAsync(folder.Path);
            _recoveryActive = false;
            RecoveryBar.IsOpen = false;
            UpdateChrome();
            Select("home");
            Navigate("home");
        }
        catch (Exception ex)
        {
            RecoveryMessage.Text = $"L'ouverture de « {folder.Path} » a échoué : {ex.Message} " +
                "Memo reste en démonstration ; vos fichiers n'ont pas été modifiés.";
            RecoveryBar.IsOpen = true;
        }
    }

    /// <summary>Accepte la démonstration pour cette session et referme l'alerte.</summary>
    private void OnContinueDemo(object sender, RoutedEventArgs e)
    {
        _recoveryActive = false;
        RecoveryBar.IsOpen = false;
        UpdateChrome();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            Navigate(tag);
        }
    }

    public void Navigate(string tag)
    {
        _current = tag;
        var type = tag switch
        {
            "home" => typeof(HomePage),
            "library" => typeof(LibraryPage),
            "albums" => typeof(AlbumsPage),
            "recaps" => typeof(RecapsPage),
            "events" => typeof(EventsPage),
            "search" => typeof(SearchPage),
            "map" => typeof(MapPage),
            "imports" => typeof(ImportsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage)
        };

        if (ContentFrame.CurrentSourcePageType != type)
        {
            ContentFrame.Navigate(type, null, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
        }

        if (tag == "imports")
        {
            DemoBar.IsOpen = false;
        }
    }

    public async Task RunImportAsync(IReadOnlyList<string> paths)
    {
        OpenImports();
        if (ContentFrame.Content is ImportsPage page)
        {
            await page.ImportPathsAsync(paths);
        }
    }

    public void OpenImports()
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((string?)item.Tag == "imports")
            {
                Nav.SelectedItem = item;
                break;
            }
        }
        Navigate("imports");
    }

    public void Select(string tag)
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((string?)item.Tag == tag)
            {
                Nav.SelectedItem = item;
                return;
            }
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        OpenImports();
    }
}





