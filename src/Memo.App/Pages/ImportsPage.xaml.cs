using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
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
    // WinUI n'autorise qu'un ContentDialog à la fois : ce verrou évite qu'un
    // double clic ou un sélecteur encore ouvert fasse planter la page.
    private bool _dialogOpen;

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
        LibraryPanel.Visibility = hasLibrary ? Visibility.Visible : Visibility.Collapsed;
        ArchivePanel.Visibility = hasLibrary ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = hasLibrary;
        UpdateLibraryLocationText();
        IntroText.Text = hasLibrary
            ? "Ajoutez les ZIP d'un même export, dans l'ordre que vous voulez. Les archives suivantes complètent la bibliothèque sans effacer vos choix."
            : "Créez ou ouvrez une bibliothèque pour commencer. Vous pourrez ensuite y ajouter les archives de votre export.";
    }

    /// <summary>
    /// Affiche le dossier réel de la bibliothèque et l'espace libre du disque.
    /// Le choix du disque reste visible même quand la sélection tient.
    /// </summary>
    private void UpdateLibraryLocationText()
    {
        if (AppState.Current.Library is null || string.IsNullOrEmpty(AppState.Current.LibraryPath))
        {
            LibraryPathText.Text = string.Empty;
            return;
        }

        var free = GetFreeSpaceOnLibraryDrive();
        LibraryPathText.Text = free is long bytes
            ? $"{AppState.Current.LibraryPath} · {FormatSize(bytes)} libres"
            : AppState.Current.LibraryPath;
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

    private long _stagedBytes;
    private int _stagedArchives;

    private void UpdateSelection()
    {
        var hasLibrary = AppState.Current.Library is not null;
        SelectionPanel.Visibility = hasLibrary && _staged.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var rows = new List<object>();
        _stagedBytes = 0;
        _stagedArchives = 0;

        foreach (var path in _staged)
        {
            if (Directory.Exists(path))
            {
                var archives = Directory
                    .EnumerateFiles(path, "*.zip", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var size = archives.Sum(file => new FileInfo(file).Length);
                _stagedBytes += size;
                _stagedArchives += archives.Count;
                rows.Add(new
                {
                    Name = DescribePath(path).Name,
                    Detail = $"{archives.Count} archive(s) · {FormatSize(size)}"
                });
            }
            else if (File.Exists(path))
            {
                var size = new FileInfo(path).Length;
                _stagedBytes += size;
                _stagedArchives++;
                rows.Add(new
                {
                    Name = DescribePath(path).Name,
                    Detail = FormatSize(size)
                });
            }
            else
            {
                rows.Add(new
                {
                    Name = DescribePath(path).Name,
                    Detail = "Introuvable"
                });
            }
        }

        SelectionList.ItemsSource = rows;
        SelectionTitle.Text = _stagedArchives == 1
            ? "1 archive sélectionnée"
            : $"{_stagedArchives} archives sélectionnées";
        SelectionSummary.Text = $"{_stagedArchives} archive(s) · {FormatSize(_stagedBytes)}";

        var free = GetFreeSpaceOnLibraryDrive();
        if (free is long available && _stagedBytes > 0)
        {
            var required = (long)(_stagedBytes * 1.15) + 1_000_000_000;
            if (required > available)
            {
                SelectionWarning.Text =
                    $"Espace insuffisant sur le disque de la bibliothèque : {FormatSize(available)} libres, " +
                    $"environ {FormatSize(required)} nécessaires. Choisissez un autre disque ci-dessus ; " +
                    "la sélection actuelle sera conservée.";
                SelectionWarning.Visibility = Visibility.Visible;
            }
            else
            {
                SelectionWarning.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            SelectionWarning.Visibility = Visibility.Collapsed;
        }

        UpdateLibraryLocationText();
    }

    private static long? GetFreeSpaceOnLibraryDrive()
    {
        try
        {
            var root = AppState.Current.LibraryPath;
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }
            var driveRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(driveRoot))
            {
                return null;
            }
            return new DriveInfo(driveRoot).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
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

        var free = GetFreeSpaceOnLibraryDrive();
        if (free is long availableBytes && _stagedBytes > 0)
        {
            var required = (long)(_stagedBytes * 1.15) + 1_000_000_000;
            if (required > availableBytes)
            {
                Notice.Title = "Espace disque insuffisant";
                Notice.Message = $"Il reste {FormatSize(availableBytes)} sur le disque de la bibliothèque, " +
                                 $"mais environ {FormatSize(required)} sont nécessaires. Choisissez un autre disque ci-dessus.";
                Notice.Severity = InfoBarSeverity.Error;
                Notice.IsOpen = true;
                return;
            }
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
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
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
    /// Crée une bibliothèque sur un autre disque sans quitter l'écran d'import.
    /// La sélection d'archives reste en place : après la bascule, le contrôle
    /// d'espace est recalculé et l'import peut démarrer sur le nouveau disque.
    /// </summary>
    private async void OnChangeDisk(object sender, RoutedEventArgs e)
    {
        if (AppState.Current.Library is null)
        {
            return;
        }

        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
            .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (drives.Count == 0)
        {
            ShowLibraryError("Aucun disque disponible", "Aucun disque accessible n'a été trouvé.");
            return;
        }

        var required = _stagedBytes > 0 ? (long)(_stagedBytes * 1.15) + 1_000_000_000 : 0;
        // La sélection appartient à l'écran, pas à la bibliothèque : on la
        // fige ici pour la restaurer après la bascule, même si un rafraîchissement
        // intermédiaire vide la liste.
        var stagedSnapshot = _staged.ToList();
        var archiveCount = _stagedArchives;
        var driveBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var drive in drives)
        {
            driveBox.Items.Add(new ComboBoxItem
            {
                Content = $"{drive.Name.TrimEnd('\\')} · {FormatSize(drive.AvailableFreeSpace)} libres sur {FormatSize(drive.TotalSize)}",
                Tag = drive
            });
        }

        // Proposer d'abord un disque qui peut réellement recevoir la sélection,
        // sinon celui qui offre le plus de place.
        var preferred = drives
            .Where(drive => required == 0 || drive.AvailableFreeSpace >= required)
            .OrderByDescending(drive => drive.AvailableFreeSpace)
            .FirstOrDefault() ?? drives[0];
        driveBox.SelectedIndex = drives.IndexOf(preferred);

        var nameBox = new TextBox
        {
            Header = "Nom du dossier",
            Text = "Memo",
            MaxLength = 80
        };
        var preview = new TextBlock
        {
            Style = Widgets.Style("MemoCaptionTextStyle"),
            TextWrapping = TextWrapping.Wrap
        };

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "La bibliothèque actuelle reste intacte. Les prochains imports seront enregistrés sur le disque choisi ; la sélection d'archives est conservée.",
            TextWrapping = TextWrapping.Wrap,
            Style = Widgets.Style("MemoSecondaryTextStyle")
        });
        content.Children.Add(driveBox);
        content.Children.Add(nameBox);
        content.Children.Add(preview);

        var dialog = new ContentDialog
        {
            Title = "Choisir un autre disque",
            Content = content,
            PrimaryButtonText = "Créer la bibliothèque",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        void RefreshPreview()
        {
            if (driveBox.SelectedItem is not ComboBoxItem { Tag: DriveInfo drive })
            {
                return;
            }

            var safe = SafeFolderName(nameBox.Text);
            if (safe.Length == 0)
            {
                preview.Text = "Saisissez un nom de dossier valide.";
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }

            var root = Path.Combine(drive.Name, safe);
            var folderAvailable = true;
            var openExisting = false;
            string? issue = null;
            try
            {
                if (Directory.Exists(root))
                {
                    // Un dossier qui contient déjà un catalogue Memo est une
                    // bibliothèque existante : on la propose à l'ouverture au
                    // lieu de refuser un dossier non vide.
                    openExisting =
                        File.Exists(Path.Combine(root, "library.json")) &&
                        File.Exists(Path.Combine(root, "catalog.db"));

                    if (!openExisting && Directory.EnumerateFileSystemEntries(root).Any())
                    {
                        folderAvailable = false;
                        issue = "Ce dossier existe déjà et n'est pas vide. Choisissez un autre nom.";
                    }
                }
            }
            catch (Exception ex)
            {
                folderAvailable = false;
                issue = $"Dossier illisible : {ex.Message}";
            }

            if (required > drive.AvailableFreeSpace)
            {
                var space = $"Espace insuffisant pour la sélection actuelle : {FormatSize(required)} nécessaires.";
                issue = issue is null ? space : $"{issue} {space}";
            }

            dialog.PrimaryButtonText = openExisting ? "Ouvrir cette bibliothèque" : "Créer la bibliothèque";
            preview.Text = openExisting
                ? $"{root} · bibliothèque Memo existante"
                : issue is null
                    ? $"{root} · {FormatSize(drive.AvailableFreeSpace)} libres"
                    : $"{root} · {issue}";
            dialog.IsPrimaryButtonEnabled = folderAvailable;
        }

        driveBox.SelectionChanged += (_, _) => RefreshPreview();
        nameBox.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        if (driveBox.SelectedItem is not ComboBoxItem { Tag: DriveInfo selectedDrive })
        {
            return;
        }

        var folderName = SafeFolderName(nameBox.Text);
        if (folderName.Length == 0)
        {
            return;
        }

        var destination = Path.Combine(selectedDrive.Name, folderName);
        var existingLibrary =
            File.Exists(Path.Combine(destination, "library.json")) &&
            File.Exists(Path.Combine(destination, "catalog.db"));
        try
        {
            if (existingLibrary)
            {
                await AppState.Current.OpenLibraryAsync(destination);
            }
            else
            {
                await AppState.Current.CreateLibraryAsync(destination, nameBox.Text.Trim());
            }

            if (stagedSnapshot.Count > 0)
            {
                _staged.Clear();
                _staged.AddRange(stagedSnapshot);
            }

            var selection = archiveCount > 0
                ? $"Les {archiveCount} archive(s) sélectionnées sont conservées."
                : "Aucune archive n'était sélectionnée.";
            var action = existingLibrary ? "Bibliothèque ouverte" : "Bibliothèque créée";
            await AfterLibraryReadyAsync(
                $"{action} sur {selectedDrive.Name.TrimEnd('\\')}",
                $"Les prochains imports seront enregistrés dans {destination}. {selection}");
        }
        catch (Exception ex)
        {
            ShowLibraryError("Création impossible", ex.Message);
        }
    }

    private static string SafeFolderName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var safe = string.Join('-', trimmed.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return safe.TrimEnd('.', ' ');
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
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
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

    /// <summary>
    /// Ouvre un dialogue unique. Une deuxième demande pendant qu'un dialogue
    /// est déjà ouvert est ignorée avec un message au lieu de lever
    /// « Only a single ContentDialog can be open at any time ».
    /// </summary>
    private async Task<ContentDialogResult?> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen)
        {
            ShowLibraryError(
                "Dialogue déjà ouvert",
                "Fermez la fenêtre déjà affichée avant d'en ouvrir une autre.");
            return null;
        }

        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (COMException)
        {
            ShowLibraryError(
                "Ouverture impossible",
                "Une autre fenêtre est encore ouverte. Fermez-la, puis réessayez.");
            return null;
        }
        finally
        {
            _dialogOpen = false;
        }
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



