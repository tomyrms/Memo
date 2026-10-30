using Memo.App.Pages;
using Memo.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Memo.App;

public sealed partial class ShellPage : UserControl
{
    private string _current = "home";

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
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last))
        {
            try
            {
                await AppState.Current.OpenLibraryAsync(last);
            }
            catch
            {
                AppState.Current.LoadDemo();
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

    private void UpdateChrome()
    {
        LibraryLabel.Text = AppState.Current.LibraryName;
        StorageLabel.Text = $"{AppState.Current.Cards.Count} souvenirs";
        DemoBar.IsOpen = AppState.Current.IsDemo;
        DemoBadge.Visibility = AppState.Current.IsDemo ? Visibility.Visible : Visibility.Collapsed;
        App.MainWindow?.ApplyTheme(AppState.Current.Settings.Theme);
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




