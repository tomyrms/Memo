using Memo.App.Services;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Memo.App.Pages;

public sealed partial class ImportsPage : Page
{
    private readonly List<string> _staged = new();
    private CancellationTokenSource? _cts;

    public ImportsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await RefreshSpacesAsync();
            UpdateSelection();
            await UpdateHistoryAsync();
        };
    }

    private async Task RefreshSpacesAsync()
    {
        SpaceBox.Items.Clear();
        if (AppState.Current.Library is null)
        {
            SpaceBox.Items.Add(new ComboBoxItem { Content = "Aucune bibliothèque ouverte", Tag = 0L, IsSelected = true });
            ImportButton.IsEnabled = false;
            SelectionPanel.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var space in AppState.Current.Spaces)
        {
            SpaceBox.Items.Add(new ComboBoxItem { Content = space.Name, Tag = space.Id });
        }
        SpaceBox.SelectedIndex = SpaceBox.Items.Count > 0 ? 0 : -1;
        ImportButton.IsEnabled = true;
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

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_staged.Count == 0)
        {
            return;
        }

        if (AppState.Current.Library is null)
        {
            Notice.Title = "Bibliothèque non ouverte";
            Notice.Message = "Créez ou ouvrez une bibliothèque depuis Réglages avant de lancer l'import.";
            Notice.Severity = InfoBarSeverity.Warning;
            Notice.IsOpen = true;
            return;
        }

        var spaceId = (SpaceBox.SelectedItem as ComboBoxItem)?.Tag as long? ?? AppState.Current.CurrentSpaceId;
        _cts = new CancellationTokenSource();
        ImportButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
        ProgressBar.IsIndeterminate = true;
        ProgressTitle.Text = "Inventaire des archives";
        ProgressText.Text = "Lecture de l'archive en cours…";

        var progress = new Progress<ImportProgress>(UpdateProgress);
        try
        {
            var result = await AppState.Current.ImportAsync(spaceId, _staged.ToList(), progress, _cts.Token);
            ShowResult(result);
            _staged.Clear();
            UpdateSelection();
            await UpdateHistoryAsync();
        }
        catch (OperationCanceledException)
        {
            Notice.Title = "Import annulé";
            Notice.Message = "Les éléments déjà validés restent disponibles ; relancer l'import ne créera pas de doublon.";
            Notice.Severity = InfoBarSeverity.Informational;
            Notice.IsOpen = true;
        }
        catch (Exception ex)
        {
            Notice.Title = "Import interrompu";
            Notice.Message = ex.Message;
            Notice.Severity = InfoBarSeverity.Error;
            Notice.IsOpen = true;
        }
        finally
        {
            ImportButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void UpdateProgress(ImportProgress progress)
    {
        ProgressTitle.Text = progress.Phase switch
        {
            ImportPhase.Scanning => "Inventaire des archives",
            ImportPhase.Reading => "Lecture des fichiers",
            ImportPhase.Storing => "Copie des médias",
            ImportPhase.Associating => "Association des calques",
            ImportPhase.Analyzing => "Préparation des miniatures",
            ImportPhase.Done => "Import terminé",
            ImportPhase.Failed => "Import interrompu",
            ImportPhase.Cancelled => "Import annulé",
            _ => "Import en cours"
        };
        ProgressBar.IsIndeterminate = progress.FilesTotal <= 0;
        if (progress.FilesTotal > 0)
        {
            ProgressBar.Maximum = progress.FilesTotal;
            ProgressBar.Value = Math.Min(progress.FilesProcessed, progress.FilesTotal);
        }
        ProgressText.Text = $"{progress.FilesProcessed} / {progress.FilesTotal} fichiers · {progress.Message}";
    }

    private void ShowResult(ImportResult result)
    {
        ResultPanel.Visibility = Visibility.Visible;
        StatMemories.Text = result.Counters.MemoriesCreated.ToString();
        StatMedia.Text = result.Counters.MediaImported.ToString();
        StatOverlays.Text = result.Counters.OverlaysImported.ToString();
        var missing = AppState.Current.Cards.Count(c => c.Status == MemoryStatus.OverlayOnly);
        StatMissing.Text = missing.ToString();
        ResultNote.Text = $"{result.Counters.MemoriesUpdated} souvenir(s) mis à jour · {result.Counters.DuplicatesSkipped} doublon(s) ignoré(s). " +
                          "Sans manifeste d'export, la complétude de l'ensemble ne peut pas être affirmée.";
        Diagnostics.ItemsSource = result.Diagnostics.Select(d => new
        {
            Title = d.Code,
            Detail = d.Message + (string.IsNullOrEmpty(d.EntryPath) ? string.Empty : $" ({d.EntryPath})")
        }).ToList();
        var skipped = result.Counters.Errors;
        Notice.Title = result.Cancelled ? "Import partiel" : "Import terminé";
        Notice.Message = skipped == 0
            ? "Les archives traitées sont dans la bibliothèque. Les éléments non associés restent visibles."
            : $"{skipped} erreur(s) de lecture ; consultez le bilan ci-dessous.";
        Notice.Severity = skipped == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private async void OnNewSpace(object sender, RoutedEventArgs e)
    {
        if (AppState.Current.Library is null)
        {
            Notice.Title = "Aucune bibliothèque";
            Notice.Message = "Créez une bibliothèque depuis Réglages avant d'ajouter un espace source.";
            Notice.Severity = InfoBarSeverity.Warning;
            Notice.IsOpen = true;
            return;
        }

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
            try
            {
                await AppState.Current.Library.Catalog.CreateSpaceAsync(box.Text.Trim());
                await AppState.Current.ReloadAsync();
                await RefreshSpacesAsync();
                Notice.Title = "Espace ajouté";
                Notice.Message = "Les archives suivantes seront rattachées à cet espace ; les identifiants ne seront pas fusionnés avec ceux d'un autre espace.";
                Notice.Severity = InfoBarSeverity.Success;
                Notice.IsOpen = true;
            }
            catch (Exception ex)
            {
                Notice.Title = "Espace non créé";
                Notice.Message = ex.Message;
                Notice.Severity = InfoBarSeverity.Error;
                Notice.IsOpen = true;
            }
        }
    }

    private static string StatusLabel(ImportBatchInfo batch)
    {
        if (batch.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            return batch.Memories > 0 ? "Partiel" : "Échec";
        }
        return batch.Status.ToLowerInvariant() switch
        {
            "completed" => "Terminé",
            "processing" => "En cours",
            "cancelled" => "Annulé",
            _ => batch.Status
        };
    }

    private async Task UpdateHistoryAsync()
    {
        if (AppState.Current.Library is null)
        {
            History.ItemsSource = null;
            HistoryEmpty.Visibility = Visibility.Visible;
            HistoryEmpty.Text = "Aucun import : commencez par créer ou ouvrir une bibliothèque.";
            return;
        }

        var batches = await AppState.Current.Library.Catalog.GetBatchesAsync();
        History.ItemsSource = batches.Select(b => new
        {
            Title = $"{b.SpaceName} · {b.StartedAt.LocalDateTime:g}",
            Detail = $"{StatusLabel(b)} · {b.Archives} archive(s) · {b.Memories} souvenir(s)"
        }).ToList();
        HistoryEmpty.Visibility = batches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Text = "Aucun import enregistré.";
    }
}

