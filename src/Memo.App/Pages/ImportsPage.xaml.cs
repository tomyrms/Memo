using Memo.App.Services;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Memo.App.Pages;

public sealed partial class ImportsPage : Page
{
    private readonly List<string> _staged = new();

    public ImportsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (SpaceBox.Items.Count == 0)
            {
                SpaceBox.Items.Add(new ComboBoxItem { Content = "Démonstration", Tag = 1L, IsSelected = true });
            }
            SpaceBox.SelectedIndex = 0;
            UpdateSelection();
            UpdateHistory();
        };
    }

    private async void OnPickArchives(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add(".zip");
        Initialize(picker);
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
        {
            if (!_staged.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
            {
                _staged.Add(file.Path);
            }
        }
        UpdateSelection();
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        Initialize(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && !_staged.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
        {
            _staged.Add(folder.Path);
        }
        UpdateSelection();
    }

    private static void Initialize(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private void UpdateSelection()
    {
        SelectionPanel.Visibility = _staged.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionTitle.Text = _staged.Count == 1 ? "1 archive sélectionnée" : $"{_staged.Count} archives sélectionnées";
        SelectionList.ItemsSource = _staged.Select(p => new
        {
            Name = Path.GetFileName(p.TrimEnd('\\')),
            Detail = Directory.Exists(p) ? "Dossier" : FormatSize(new FileInfo(p).Length)
        }).ToList();
    }

    private static string FormatSize(long bytes)
        => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.##} Go" : bytes >= 1_000_000 ? $"{bytes / 1e6:0.#} Mo" : $"{bytes / 1000:0} Ko";

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        _staged.Clear();
        UpdateSelection();
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (_staged.Count == 0)
        {
            return;
        }

        Notice.Title = "Bibliothèque non ouverte";
        Notice.Message = "L'import écrit dans une bibliothèque locale. Créez-la ou ouvrez-la depuis Réglages avant de lancer l'import.";
        Notice.Severity = InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        Notice.Title = "Import annulé";
        Notice.Message = "Les éléments déjà validés resteraient disponibles ; relancer l'import ne créerait pas de doublon.";
        Notice.Severity = InfoBarSeverity.Informational;
        Notice.IsOpen = true;
    }

    private async void OnNewSpace(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "Nom de l'espace (ex. Perso 2018-2023)", MaxLength = 80 };
        var dialog = new ContentDialog
        {
            Title = "Nouvel espace source",
            Content = box,
            PrimaryButtonText = "Créer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            var id = SpaceBox.Items.Count + 1L;
            SpaceBox.Items.Add(new ComboBoxItem { Content = box.Text.Trim(), Tag = id });
            SpaceBox.SelectedIndex = SpaceBox.Items.Count - 1;
            Notice.Title = "Espace ajouté";
            Notice.Message = "Cet espace sera utilisé pour les archives sélectionnées. Les identifiants ne seront pas fusionnés avec ceux d'un autre espace.";
            Notice.Severity = InfoBarSeverity.Success;
            Notice.IsOpen = true;
        }
    }

    private void UpdateHistory()
    {
        var items = AppState.Current.Cards.Count == 0
            ? null
            : new[]
            {
                new { Title = "Démonstration", Detail = "22 souvenirs synthétiques · aucun fichier importé" }
            };
        History.ItemsSource = items;
        HistoryEmpty.Visibility = items is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
