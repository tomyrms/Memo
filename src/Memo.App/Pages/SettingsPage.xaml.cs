using Memo.App.Analysis;
using Memo.App.Notifications;
using Memo.App.Services;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Memo.App.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        _loading = true;
        var settings = AppState.Current.Settings;
        TimeZoneBox.Items.Clear();

        var seenZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones().OrderBy(z => z.DisplayName, StringComparer.CurrentCulture))
        {
            var iana = TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var converted) ? converted : zone.Id;
            if (!seenZones.Add(iana))
            {
                continue;
            }
            TimeZoneBox.Items.Add(new ComboBoxItem { Content = zone.DisplayName, Tag = iana });
        }
        SelectByTag(TimeZoneBox, settings.TimeZoneId);
        SelectByTag(ThemeBox, settings.Theme);

        PhotoDuration.Value = settings.PhotoDurationSeconds;
        AutoplaySwitch.IsOn = settings.AutoplayVideos;
        AudioSwitch.IsOn = settings.PlayAudio;
        MotionSwitch.IsOn = settings.ReduceMotion;
        RemindersSwitch.IsOn = settings.RemindersEnabled;
        DiscreetSwitch.IsOn = settings.DiscreetNotifications;
        OcrSwitch.IsOn = settings.OcrEnabled;
        FacesSwitch.IsOn = settings.FacesEnabled;
        if (TimeSpan.TryParse(settings.ReminderTime, out var time))
        {
            ReminderTime.SelectedTime = time;
        }

        VersionLabel.Text = "Memo 0.1.0 · Windows 10 21H2 et Windows 11 · x64";
        LibraryPath.Text = AppState.Current.IsDemo
            ? "Bibliothèque de démonstration (aucun fichier importé)"
            : AppState.Current.LibraryPath ?? "Bibliothèque locale";
        var cards = AppState.Current.Cards.Count;
        LibraryStats.Text = AppState.Current.IsDemo
            ? $"{cards} souvenirs synthétiques · aucune donnée personnelle"
            : $"{cards} souvenirs · {AppState.Current.Albums.Count} album(s)";

        _loading = false;
        _ = UpdateReminderStatusAsync();
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }

        // Les identifiants IANA et Windows ne se correspondent pas toujours
        // (Europe/Zurich -> W. Europe Standard Time -> Europe/Berlin).
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(tag, out var windowsId))
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is string itemTag &&
                    TimeZoneInfo.TryConvertIanaIdToWindowsId(itemTag, out var itemWindows) &&
                    string.Equals(itemWindows, windowsId, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = item;
                    return;
                }
            }
        }

        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        var tag = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
        App.MainWindow?.ApplyTheme(tag);
        await SaveAsync(AppState.Current.Settings with { Theme = tag });
    }

    private async void OnMotionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        await SaveAsync(AppState.Current.Settings with { ReduceMotion = MotionSwitch.IsOn });
    }

    private async void OnPhotoDurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
        {
            return;
        }
        await SaveAsync(AppState.Current.Settings with { PhotoDurationSeconds = (int)args.NewValue });
    }

    private async void OnPlaybackChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        await SaveAsync(AppState.Current.Settings with
        {
            AutoplayVideos = AutoplaySwitch.IsOn,
            PlayAudio = AudioSwitch.IsOn
        });
    }

    private async void OnTimeZoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        if (TimeZoneBox.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            try
            {
                await AppState.Current.SetTimeZoneAsync(id);
                Show("Fuseau de classement modifié",
                    "Les jours ont été recalculés à partir des instants conservés. Aucune date d'origine n'est modifiée.",
                    InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                Show("Changement de fuseau impossible", ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    private async void OnRemindersChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var settings = AppState.Current.Settings with { RemindersEnabled = RemindersSwitch.IsOn };
        await SaveAsync(settings);

        if (AppState.Current.Library is null)
        {
            if (RemindersSwitch.IsOn)
            {
                Show("Rappel non planifié", "Ouvrez une bibliothèque avant d'activer un rappel.", InfoBarSeverity.Warning);
            }
            return;
        }

        try
        {
            if (RemindersSwitch.IsOn)
            {
                var result = await ReminderScheduler.EnableAsync(
                    Environment.ProcessPath ?? "Memo.exe",
                    AppState.Current.Library.Info.RootPath,
                    settings.ReminderTime);
                if (!result.Success)
                {
                    Show("Rappel non planifié", result.Message, InfoBarSeverity.Error);
                }
                else
                {
                    Show("Rappel activé", $"Une tâche Windows quotidienne est planifiée à {settings.ReminderTime}. Elle ne s'exécute que si Windows est allumé et l'utilisateur connecté.", InfoBarSeverity.Success);
                }
            }
            else
            {
                await ReminderScheduler.DisableAsync(AppState.Current.Library.Info.Id);
                Show("Rappel désactivé", "La tâche planifiée a été supprimée.", InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            Show("Rappel non configuré", ex.Message, InfoBarSeverity.Error);
        }

        await UpdateReminderStatusAsync();
    }

    private async void OnReminderTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }

        var time = (args.NewTime ?? TimeSpan.FromHours(9)).ToString(@"hh\:mm");
        var settings = AppState.Current.Settings with { ReminderTime = time };
        await SaveAsync(settings);

        if (settings.RemindersEnabled && AppState.Current.Library is not null)
        {
            try
            {
                await ReminderScheduler.DisableAsync(AppState.Current.Library.Info.Id);
                await ReminderScheduler.EnableAsync(
                    Environment.ProcessPath ?? "Memo.exe",
                    AppState.Current.Library.Info.RootPath,
                    time);
            }
            catch (Exception ex)
            {
                Show("Heure non appliquée", ex.Message, InfoBarSeverity.Warning);
            }
        }
        await UpdateReminderStatusAsync();
    }

    private async void OnTestReminder(object sender, RoutedEventArgs e)
    {
        if (AppState.Current.Library is null)
        {
            Show("Aucune bibliothèque", "Ouvrez une bibliothèque avant de tester un rappel.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var result = await ReminderRunner.RunAsync(
                AppState.Current.Library.Info.RootPath,
                new ReminderRunOptions { IgnoreRemindersSetting = true, IgnoreAlreadyNotified = true });
            Show(result.NotificationShown ? "Notification envoyée" : "Notification non envoyée",
                result.Diagnostic,
                result.NotificationShown ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Show("Test impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task UpdateReminderStatusAsync()
    {
        if (AppState.Current.Library is null)
        {
            ReminderStatus.Text = "Aucune bibliothèque ouverte.";
            return;
        }

        try
        {
            var status = await ReminderScheduler.GetStatusAsync(AppState.Current.Library.Info.Id);
            ReminderStatus.Text = status.Exists
                ? $"Tâche planifiée · {status.StartTime} · {status.LastResultText}"
                : "Aucune tâche planifiée.";
        }
        catch (Exception ex)
        {
            ReminderStatus.Text = "État indisponible : " + ex.Message;
        }
    }

    private async void OnDiscreetChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        await SaveAsync(AppState.Current.Settings with { DiscreetNotifications = DiscreetSwitch.IsOn });
    }

    private async void OnAnalysisChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        await SaveAsync(AppState.Current.Settings with
        {
            OcrEnabled = OcrSwitch.IsOn,
            FacesEnabled = FacesSwitch.IsOn
        });
    }

    private async Task SaveAsync(AppSettings settings)
    {
        try
        {
            await AppState.Current.SaveSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            Show("Réglage non enregistré", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async void OnOpenLibrary(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }
        try
        {
            await AppState.Current.OpenLibraryAsync(folder.Path);
            Initialize();
            Show("Bibliothèque ouverte", $"{AppState.Current.Cards.Count} souvenir(s) chargés depuis {folder.Path}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Ouverture impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

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
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var parent = await picker.PickSingleFolderAsync();
        if (parent is null)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        var safe = string.Join('-', name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safe))
        {
            Show("Création impossible", "Le nom choisi ne permet pas de créer un dossier.", InfoBarSeverity.Error);
            return;
        }
        var root = Path.Combine(parent.Path, safe);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            Show("Dossier déjà utilisé", $"Le dossier {root} n'est pas vide. Choisissez un autre emplacement.", InfoBarSeverity.Error);
            return;
        }

        try
        {
            await AppState.Current.CreateLibraryAsync(root, name);
            Initialize();
            Show("Bibliothèque créée", $"Les fichiers seront stockés dans {root}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Création impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnBackup(object sender, RoutedEventArgs e)
    {
        if (AppState.Current.IsDemo)
        {
            Show("Aucune bibliothèque", "Créez ou ouvrez une bibliothèque avant de la sauvegarder.", InfoBarSeverity.Warning);
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"memo-sauvegarde-{DateTime.Now:yyyyMMdd}"
        };
        picker.FileTypeChoices.Add("Archive ZIP", new List<string> { ".zip" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            Show("Sauvegarde en cours", "Le catalogue et les médias sources sont copiés.", InfoBarSeverity.Informational);
            var path = await AppState.Current.BackupAsync(file.Path);
            Show("Sauvegarde terminée", $"Archive créée : {path}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Sauvegarde impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        var open = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        open.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(open, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await open.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var folderPicker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        folderPicker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var parent = await folderPicker.PickSingleFolderAsync();
        if (parent is null)
        {
            return;
        }

        var target = Path.Combine(parent.Path, $"Memo-restaure-{DateTime.Now:yyyyMMdd-HHmmss}");

        // La restauration ne dépend pas de la bibliothèque ouverte : elle reste
        // disponible au premier lancement, en mode démonstration.
        try
        {
            Show("Restauration en cours", "La sauvegarde est vérifiée puis extraite dans un dossier vide.", InfoBarSeverity.Informational);
            await AppState.Current.RestoreAsync(file.Path, target);
        }
        catch (Exception ex)
        {
            // Aucune écriture n'a touché la bibliothèque en cours : elle reste ouverte.
            Show("Restauration impossible", ex.Message, InfoBarSeverity.Error);
            return;
        }

        try
        {
            // L'ouverture bascule seulement si la bibliothèque restaurée est
            // entièrement lisible ; sinon l'ancienne reste active.
            await AppState.Current.OpenLibraryAsync(target);
            Initialize();
            Show("Restauration terminée", $"{AppState.Current.Cards.Count} souvenir(s) restaurés dans {target}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Sauvegarde restaurée mais non ouverte",
                $"Les fichiers ont été extraits dans {target}, mais la bibliothèque n'a pas pu être ouverte : {ex.Message}",
                InfoBarSeverity.Warning);
        }
    }
    private async void OnOpenLicenses(object sender, RoutedEventArgs e)
    {
        var text = "Memo 0.1.0\n\n" +
                   "Windows App SDK — MIT\n" +
                   "SkiaSharp — MIT\n" +
                   "SQLite / Microsoft.Data.Sqlite — domaine public / MIT\n" +
                   "LibVLCSharp — LGPL 2.1 ou ultérieure\n" +
                   "LibVLC (VideoLAN) — LGPL 2.1 ou ultérieure, DLL séparées et remplaçables\n" +
                   "CommunityToolkit.Mvvm — MIT\n\n" +
                   "Les textes complets sont livrés dans le dossier LICENSES de l'installation.";
        var dialog = new ContentDialog
        {
            Title = "Composants et licences",
            Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, MaxHeight = 420 },
            CloseButtonText = "Fermer",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private CancellationTokenSource? _analysisCts;

    private async void OnAnalyze(object sender, RoutedEventArgs e)
    {
        if (AppState.Current.Library is null)
        {
            Show("Aucune bibliothèque", "Ouvrez une bibliothèque avant de lancer l'analyse.", InfoBarSeverity.Warning);
            return;
        }

        _analysisCts = new CancellationTokenSource();
        AnalyzeButton.IsEnabled = false;
        CancelAnalysisButton.Visibility = Visibility.Visible;
        AnalysisProgress.Visibility = Visibility.Visible;
        AnalysisProgress.Value = 0;
        AnalysisStatus.Text = "Préparation…";

        try
        {
            var runner = new AnalysisRunner(AppState.Current.Library);
            var options = new AnalysisRunOptions
            {
                Ocr = OcrSwitch.IsOn,
                VisualHash = true,
                Faces = FacesSwitch.IsOn,
                SkipExisting = false
            };
            var progress = new Progress<AnalysisProgress>(p =>
            {
                AnalysisProgress.Maximum = Math.Max(1, p.Total);
                AnalysisProgress.Value = Math.Min(p.Done, p.Total);
                AnalysisStatus.Text = $"{p.Message} ({p.Done}/{p.Total})";
            });
            var summary = await runner.RunAsync(options, progress, _analysisCts.Token);
            AnalysisStatus.Text = $"{summary.OcrCompleted} OCR · {summary.VisualHashesCompleted} empreintes · {summary.FacesDetected} visages · {summary.FaceGroupsCreated} groupes";
            Show("Analyse terminée",
                summary.Error ?? $"Traitement local terminé en {summary.Duration.TotalSeconds:0} s. Les résultats sont corrigeables et supprimables.",
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            AnalysisStatus.Text = "Analyse annulée.";
        }
        catch (Exception ex)
        {
            AnalysisStatus.Text = "Analyse interrompue.";
            Show("Analyse impossible", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _analysisCts.Dispose();
            _analysisCts = null;
            AnalyzeButton.IsEnabled = true;
            CancelAnalysisButton.Visibility = Visibility.Collapsed;
            AnalysisProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelAnalysis(object sender, RoutedEventArgs e) => _analysisCts?.Cancel();

    private void Show(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }
}






