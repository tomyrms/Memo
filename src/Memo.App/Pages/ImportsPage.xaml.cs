using Memo.App.Services;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Memo.App.Pages;

public sealed partial class ImportsPage : Page
{
    private readonly List<string> _staged = new();
    private CancellationTokenSource? _cts;
    // Empêche le rechargement de la liste des espaces de réécrire la sélection
    // mémorisée pendant que les éléments sont ajoutés.
    private bool _refreshingSpaces;

    public ImportsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            RefreshSpaces();
            UpdateSelection();
            await UpdateHistoryAsync();
        };
    }

    /// <summary>
    /// Reconstruit la liste des espaces en sélectionnant celui mémorisé dans
    /// <see cref="AppState"/>. Le choix survit donc à la navigation et à l'import suivant.
    /// </summary>
    private void RefreshSpaces()
    {
        _refreshingSpaces = true;
        try
        {
            SpaceBox.Items.Clear();
            if (AppState.Current.Library is null)
            {
                return;
            }

            var current = AppState.Current.CurrentSpaceId;
            foreach (var space in AppState.Current.Spaces)
            {
                SpaceBox.Items.Add(new ComboBoxItem
                {
                    Content = space.Name,
                    Tag = space.Id,
                    IsSelected = space.Id == current
                });
            }

            if (SpaceBox.SelectedItem is null && SpaceBox.Items.Count > 0)
            {
                SpaceBox.SelectedIndex = 0;
            }

            if ((SpaceBox.SelectedItem as ComboBoxItem)?.Tag is long selected)
            {
                AppState.Current.SelectSpace(selected);
            }
        }
        finally
        {
            _refreshingSpaces = false;
            UpdateLibraryState();
        }
    }

    /// <summary>
    /// Reflète la présence d'une bibliothèque : sans elle, l'écran propose un
    /// vrai parcours de création ou d'ouverture au lieu de contrôles inactifs.
    /// </summary>
    private void UpdateLibraryState()
    {
        var hasLibrary = AppState.Current.Library is not null;
        NoLibraryPanel.Visibility = hasLibrary ? Visibility.Collapsed : Visibility.Visible;
        SpacePanel.Visibility = hasLibrary ? Visibility.Visible : Visibility.Collapsed;
        ArchivePanel.Visibility = hasLibrary ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = hasLibrary;
        IntroText.Text = hasLibrary
            ? "Ajoutez les ZIP d'un même export, dans l'ordre que vous voulez. Les archives suivantes complètent la bibliothèque sans effacer vos choix."
            : "Créez ou ouvrez une bibliothèque pour commencer. Vous pourrez ensuite y ajouter les archives de votre export.";

        if (!hasLibrary && _staged.Count > 0)
        {
            // La bibliothèque a été fermée : les chemins préparés ne sont plus
            // rattachables et aucun import ne peut aboutir.
            _staged.Clear();
        }
    }

    private void OnSpaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingSpaces)
        {
            return;
        }

        if ((SpaceBox.SelectedItem as ComboBoxItem)?.Tag is long spaceId)
        {
            AppState.Current.SelectSpace(spaceId);
        }
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

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Ajouter à Memo";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is StorageFile file && file.FileType.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (!_staged.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
                {
                    _staged.Add(file.Path);
                }
            }
            else if (item is StorageFolder folder)
            {
                if (!_staged.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
                {
                    _staged.Add(folder.Path);
                }
            }
        }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var hasLibrary = AppState.Current.Library is not null;
        SelectionPanel.Visibility = hasLibrary && _staged.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SelectionTitle.Text = _staged.Count == 1 ? "1 archive sélectionnée" : $"{_staged.Count} archives sélectionnées";
        SelectionList.ItemsSource = _staged.Select(path =>
        {
            var entry = DescribePath(path);
            return new { entry.Name, entry.Detail };
        }).ToList();
    }

    /// <summary>
    /// Décrit un chemin sans jamais lever d'exception : un fichier peut avoir été
    /// déplacé ou supprimé entre la sélection et l'affichage de la liste.
    /// </summary>
    private static (string Name, string Detail) DescribePath(string path)
    {
        var name = path;
        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(leaf))
            {
                name = leaf;
            }
        }
        catch (ArgumentException)
        {
            // Chemin mal formé : le libellé complet reste utilisable.
        }

        try
        {
            if (Directory.Exists(path))
            {
                return (name, "Dossier");
            }
            if (File.Exists(path))
            {
                return (name, FormatSize(new FileInfo(path).Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Fichier disparu ou illisible : il est présenté comme introuvable.
        }

        return (name, "Introuvable");
    }

    private static string FormatSize(long bytes)
        => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.##} Go" : bytes >= 1_000_000 ? $"{bytes / 1e6:0.#} Mo" : $"{bytes / 1000:0} Ko";

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        _staged.Clear();
        UpdateSelection();
    }

    private async void OnImport(object sender, RoutedEventArgs e) => await StartImportAsync();

    public async Task ImportPathsAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (!_staged.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _staged.Add(path);
            }
        }
        UpdateSelection();
        await StartImportAsync();
    }

    private async Task StartImportAsync()
    {
        if (_staged.Count == 0)
        {
            return;
        }

        if (AppState.Current.Library is null)
        {
            Notice.Title = "Bibliothèque non ouverte";
            Notice.Message = "Créez ou ouvrez une bibliothèque ci-dessus avant de lancer l'import.";
            Notice.Severity = InfoBarSeverity.Warning;
            Notice.IsOpen = true;
            return;
        }

        // Un chemin peut disparaître entre la sélection et le clic sur Importer.
        // On revérifie l'existence maintenant, on écarte les entrées perdues et
        // on les signale comme diagnostics au lieu de laisser l'import échouer.
        var missing = new List<ImportDiagnostic>();
        var available = new List<string>();
        foreach (var path in _staged)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                available.Add(path);
            }
            else
            {
                missing.Add(new ImportDiagnostic(
                    "error",
                    "path.missing",
                    "Le chemin sélectionné est introuvable au moment de l'import.",
                    path));
            }
        }

        if (missing.Count > 0)
        {
            var lost = missing
                .Select(diagnostic => diagnostic.Path)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _staged.RemoveAll(path => lost.Contains(path));
            UpdateSelection();
        }

        if (available.Count == 0)
        {
            ShowMissingOnly(missing);
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
            var result = await AppState.Current.ImportAsync(spaceId, available, progress, _cts.Token);
            ShowResult(result, missing);
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
            ImportButton.IsEnabled = AppState.Current.Library is not null;
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

    private void ShowResult(ImportResult result, IReadOnlyList<ImportDiagnostic> preDiagnostics)
    {
        ResultPanel.Visibility = Visibility.Visible;
        StatMemories.Text = result.Counters.MemoriesCreated.ToString();
        StatMedia.Text = result.Counters.MediaImported.ToString();
        StatOverlays.Text = result.Counters.OverlaysImported.ToString();
        var missingCards = AppState.Current.Cards.Count(c => c.Status == MemoryStatus.OverlayOnly);
        StatMissing.Text = missingCards.ToString();
        ResultNote.Text = $"{result.Counters.MemoriesUpdated} souvenir(s) mis à jour · {result.Counters.DuplicatesSkipped} doublon(s) ignoré(s). " +
                          "Sans manifeste d'export, la complétude de l'ensemble ne peut pas être affirmée.";
        if (preDiagnostics.Count > 0)
        {
            ResultNote.Text += $" {preDiagnostics.Count} chemin(s) introuvable(s) ont été écartés avant l'import.";
        }

        Diagnostics.ItemsSource = preDiagnostics
            .Concat(result.Diagnostics)
            .Select(ToDiagnosticRow)
            .ToList();
        var skipped = result.Counters.Errors + preDiagnostics.Count;
        Notice.Title = result.Cancelled ? "Import partiel" : "Import terminé";
        Notice.Message = skipped == 0
            ? "Les archives traitées sont dans la bibliothèque. Les éléments non associés restent visibles."
            : $"{skipped} erreur(s) de lecture ; consultez le bilan ci-dessous.";
        Notice.Severity = skipped == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        Notice.IsOpen = true;
    }

    /// <summary>
    /// Bilan d'un import qui n'a pas pu démarrer : tous les chemins préparés ont
    /// disparu. La sélection est déjà nettoyée, le diagnostic reste consultable.
    /// </summary>
    private void ShowMissingOnly(IReadOnlyList<ImportDiagnostic> diagnostics)
    {
        ResultPanel.Visibility = Visibility.Visible;
        StatMemories.Text = "0";
        StatMedia.Text = "0";
        StatOverlays.Text = "0";
        StatMissing.Text = AppState.Current.Cards.Count(c => c.Status == MemoryStatus.OverlayOnly).ToString();
        ResultNote.Text = "Aucune archive exploitable : tous les chemins sélectionnés sont introuvables. " +
                          "Ils ont été retirés de la sélection ; choisissez à nouveau les fichiers.";
        Diagnostics.ItemsSource = diagnostics.Select(ToDiagnosticRow).ToList();
        Notice.Title = "Chemins introuvables";
        Notice.Message = $"{diagnostics.Count} chemin(s) ne sont plus accessibles. Aucun import n'a été lancé ; " +
                         "les fichiers ont peut-être été déplacés, renommés ou supprimés.";
        Notice.Severity = InfoBarSeverity.Error;
        Notice.IsOpen = true;
    }

    private static object ToDiagnosticRow(ImportDiagnostic diagnostic) => new
    {
        Title = diagnostic.Code,
        Detail = diagnostic.Message
            + (string.IsNullOrEmpty(diagnostic.Path) ? string.Empty : $" ({diagnostic.Path})")
            + (string.IsNullOrEmpty(diagnostic.EntryPath) ? string.Empty : $" ({diagnostic.EntryPath})")
    };

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private async void OnNewSpace(object sender, RoutedEventArgs e)
    {
        if (AppState.Current.Library is null)
        {
            Notice.Title = "Aucune bibliothèque";
            Notice.Message = "Créez ou ouvrez une bibliothèque ci-dessus avant d'ajouter un espace source.";
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
                var created = await AppState.Current.Library.Catalog.CreateSpaceAsync(box.Text.Trim());
                await AppState.Current.ReloadAsync();

                // L'espace créé devient la sélection courante : il est visible
                // dans la liste et reste choisi pour l'import suivant.
                AppState.Current.SelectSpace(created.Id);
                RefreshSpaces();
                Notice.Title = "Espace ajouté";
                Notice.Message = $"L'espace « {created.Name} » est sélectionné. Les prochaines archives y seront rattachées ; " +
                                 "les identifiants ne seront pas fusionnés avec ceux d'un autre espace.";
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

    /// <summary>
    /// Crée une bibliothèque depuis l'écran Imports : même parcours que les
    /// Réglages, pour que le premier lancement n'impose pas de détour.
    /// </summary>
    private async void OnCreateLibrary(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "Nom de la bibliothèque", Text = "Ma bibliothèque", MaxLength = 80 };
        var dialog = new ContentDialog
        {
            Title = "Créer une bibliothèque",
            Content = nameBox,
            PrimaryButtonText = "Choisir un dossier",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return;
        }

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        Initialize(picker);
        var parent = await picker.PickSingleFolderAsync();
        if (parent is null)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        var safe = string.Join('-', name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safe))
        {
            ShowLibraryError("Création impossible", "Le nom choisi ne permet pas de créer un dossier.");
            return;
        }

        var root = Path.Combine(parent.Path, safe);
        try
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            {
                ShowLibraryError("Dossier déjà utilisé", $"Le dossier {root} n'est pas vide. Choisissez un autre emplacement.");
                return;
            }

            await AppState.Current.CreateLibraryAsync(root, name);
            await AfterLibraryReadyAsync(
                "Bibliothèque créée",
                $"Les fichiers seront stockés dans {root}. Vous pouvez maintenant choisir les archives à importer.");
        }
        catch (Exception ex)
        {
            ShowLibraryError("Création impossible", ex.Message);
        }
    }

    /// <summary>Ouvre une bibliothèque existante depuis l'écran Imports.</summary>
    private async void OnOpenLibrary(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        Initialize(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        try
        {
            await AppState.Current.OpenLibraryAsync(folder.Path);
            await AfterLibraryReadyAsync(
                "Bibliothèque ouverte",
                $"{AppState.Current.Cards.Count} souvenir(s) chargés depuis {folder.Path}.");
        }
        catch (Exception ex)
        {
            ShowLibraryError("Ouverture impossible", ex.Message);
        }
    }

    /// <summary>Rafraîchit tout l'écran après une création ou une ouverture réussie.</summary>
    private async Task AfterLibraryReadyAsync(string title, string message)
    {
        RefreshSpaces();
        UpdateSelection();
        await UpdateHistoryAsync();
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Success;
        Notice.IsOpen = true;
    }

    private void ShowLibraryError(string title, string message)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = InfoBarSeverity.Error;
        Notice.IsOpen = true;
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


