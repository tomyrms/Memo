using System.Globalization;
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
    private bool _reminderBusy;
    private int _reminderStatusVersion;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        _loading = true;
        var settings = AppState.Current.Settings;
        ReminderStatus.Text = string.Empty;
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
        if (TimeSpan.TryParseExact(
                settings.ReminderTime,
                @"hh\:mm",
                CultureInfo.InvariantCulture,
                out var time))
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
        UpdateReminderControls();
        _ = UpdateReminderStatusAsync();
        _ = RefreshAnalysisDataAsync();
    }

    private void UpdateReminderControls()
    {
        var libraryAvailable = AppState.Current.Library is not null;
        RemindersSwitch.IsEnabled = libraryAvailable && !_reminderBusy;
        ReminderTime.IsEnabled = libraryAvailable && RemindersSwitch.IsOn && !_reminderBusy;
        DiscreetSwitch.IsEnabled = libraryAvailable && RemindersSwitch.IsOn && !_reminderBusy;
        TestReminderButton.IsEnabled = libraryAvailable && !_reminderBusy;
    }

    /// <summary>Change l'interrupteur sans déclencher le gestionnaire Toggled.</summary>
    private void SetRemindersSwitch(bool value)
    {
        var loading = _loading;
        _loading = true;
        RemindersSwitch.IsOn = value;
        _loading = loading;
    }

    private void SetReminderTime(string value)
    {
        if (!TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var time))
        {
            return;
        }

        var loading = _loading;
        _loading = true;
        ReminderTime.SelectedTime = time;
        _loading = loading;
    }

    private string SelectedReminderTime()
    {
        var selected = ReminderTime.SelectedTime ?? TimeSpan.FromHours(9);
        return selected.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
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
        if (_loading || _reminderBusy)
        {
            return;
        }

        var enabled = RemindersSwitch.IsOn;
        _reminderBusy = true;
        UpdateReminderControls();
        try
        {
            var library = AppState.Current.Library;

            if (enabled && library is null)
            {
                SetRemindersSwitch(false);
                Show("Rappel non activé", "Ouvrez une bibliothèque avant d'activer un rappel quotidien.", InfoBarSeverity.Warning);
                return;
            }

            if (library is null)
            {
                await SaveAsync(AppState.Current.Settings with { RemindersEnabled = false });
                await UpdateReminderStatusAsync();
                return;
            }

            if (enabled)
            {
                var time = SelectedReminderTime();
                var result = await ReminderScheduler.EnableAsync(
                    Environment.ProcessPath ?? "Memo.exe",
                    library.Info.RootPath,
                    time);

                if (!result.Success)
                {
                    // Ne pas laisser l'interrupteur et le réglage cochés alors
                    // qu'aucune tâche ne notifiera l'utilisateur.
                    SetRemindersSwitch(false);
                    await SaveAsync(AppState.Current.Settings with { RemindersEnabled = false });
                    Show("Rappel non activé", result.Message, InfoBarSeverity.Error);
                    await UpdateReminderStatusAsync();
                    return;
                }

                await SaveAsync(AppState.Current.Settings with
                {
                    RemindersEnabled = true,
                    ReminderTime = time
                });
                Show("Rappel activé", result.Message, InfoBarSeverity.Success);
            }
            else
            {
                var result = await ReminderScheduler.DisableAsync(library.Info.Id);
                if (!result.Success)
                {
                    // La tâche peut encore se déclencher : l'interrupteur doit
                    // refléter la réalité, pas l'intention.
                    SetRemindersSwitch(true);
                    Show("Rappel toujours actif", result.Message, InfoBarSeverity.Error);
                    await UpdateReminderStatusAsync();
                    return;
                }

                await SaveAsync(AppState.Current.Settings with { RemindersEnabled = false });
                Show("Rappel désactivé", result.Message, InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            if (enabled)
            {
                SetRemindersSwitch(false);
                await SaveAsync(AppState.Current.Settings with { RemindersEnabled = false });
            }
            else
            {
                SetRemindersSwitch(true);
            }

            Show("Rappel non configuré", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _reminderBusy = false;
            UpdateReminderControls();
        }

        await UpdateReminderStatusAsync();
    }

    private async void OnReminderTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args)
    {
        if (_loading || _reminderBusy || args.NewTime is not TimeSpan selected)
        {
            return;
        }

        _reminderBusy = true;
        UpdateReminderControls();
        try
        {
            var previous = AppState.Current.Settings.ReminderTime;
            var time = selected.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            var settings = AppState.Current.Settings with { ReminderTime = time };
            await SaveAsync(settings);

            var library = AppState.Current.Library;
            if (!settings.RemindersEnabled || library is null)
            {
                return;
            }

            try
            {
                // EnableAsync recrée la tâche (/F) : inutile de la supprimer
                // avant, ce qui éviterait une fenêtre sans rappel si la
                // création échoue.
                var result = await ReminderScheduler.EnableAsync(
                    Environment.ProcessPath ?? "Memo.exe",
                    library.Info.RootPath,
                    time);

                if (!result.Success)
                {
                    // L'ancienne tâche peut encore exister à l'ancienne heure :
                    // revenir au réglage effectif plutôt que d'afficher une
                    // heure que Windows n'appliquera pas.
                    await SaveAsync(AppState.Current.Settings with { ReminderTime = previous });
                    SetReminderTime(previous);
                    Show("Heure non appliquée", result.Message, InfoBarSeverity.Error);
                }
                else
                {
                    Show("Heure du rappel modifiée",
                        $"La tâche s'exécute désormais chaque jour à {time}, même fenêtre fermée, si Windows est allumé et l'utilisateur connecté.",
                        InfoBarSeverity.Success);
                }
            }
            catch (Exception ex)
            {
                await SaveAsync(AppState.Current.Settings with { ReminderTime = previous });
                SetReminderTime(previous);
                Show("Heure non appliquée", ex.Message, InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _reminderBusy = false;
            UpdateReminderControls();
        }

        await UpdateReminderStatusAsync();
    }

    private async void OnTestReminder(object sender, RoutedEventArgs e)
    {
        var library = AppState.Current.Library;
        if (library is null)
        {
            Show("Aucune bibliothèque", "Ouvrez une bibliothèque avant de tester un rappel.", InfoBarSeverity.Warning);
            return;
        }

        TestReminderButton.IsEnabled = false;
        ReminderStatus.Text = "Test en cours…";
        try
        {
            var result = await ReminderRunner.RunAsync(
                library.Info.RootPath,
                new ReminderRunOptions
                {
                    IgnoreRemindersSetting = true,
                    IgnoreAlreadyNotified = true,
                    // Un essai ne doit ni consommer le rappel du jour ni
                    // modifier l'historique des propositions.
                    PersistState = false
                });

            if (result.NotificationShown)
            {
                ReminderStatus.Text = "Notification de test envoyée.";
                Show("Notification de test envoyée",
                    result.Diagnostic + " Le rappel du jour n'a pas été consommé par ce test.",
                    InfoBarSeverity.Success);
            }
            else
            {
                ReminderStatus.Text = "Test sans notification.";
                Show("Notification de test non affichée", result.Diagnostic, InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ReminderStatus.Text = "Test impossible.";
            Show("Test impossible", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            TestReminderButton.IsEnabled = AppState.Current.Library is not null;
        }

        await UpdateReminderStatusAsync();
    }

    private async Task UpdateReminderStatusAsync()
    {
        var version = ++_reminderStatusVersion;
        var library = AppState.Current.Library;
        var enabled = AppState.Current.Settings.RemindersEnabled;

        if (library is null)
        {
            ReminderTaskState.Text = "Aucune bibliothèque ouverte.";
            ReminderNextRun.Text = "Ouvrez une bibliothèque pour activer et vérifier un rappel.";
            ReminderLastRun.Text = string.Empty;
            TestReminderButton.IsEnabled = false;
            return;
        }

        ReminderTaskState.Text = "Vérification de la tâche planifiée…";
        ReminderNextRun.Text = string.Empty;
        ReminderLastRun.Text = string.Empty;

        try
        {
            var status = await ReminderScheduler.GetStatusAsync(library.Info.Id);
            if (version != _reminderStatusVersion)
            {
                return;
            }

            if (!status.Exists)
            {
                ReminderTaskState.Text = enabled
                    ? "Aucune tâche planifiée alors que le rappel est coché. Désactivez puis réactivez le rappel."
                    : "Aucune tâche planifiée : le rappel est désactivé.";
                ReminderNextRun.Text = "Prochaine exécution : —";
                ReminderLastRun.Text = string.IsNullOrWhiteSpace(status.Error)
                    ? "Dernier résultat : —"
                    : "État indisponible : " + status.Error;
                return;
            }

            var schedule = status.StartTime is TimeOnly start
                ? "tous les jours à " + start.ToString("HH:mm", CultureInfo.InvariantCulture)
                : "planification quotidienne";
            ReminderTaskState.Text = $"Tâche planifiée : {status.TaskName} · {schedule}"
                + (enabled ? string.Empty : " · rappel décoché dans Memo");
            ReminderNextRun.Text = status.NextRunAt is DateTimeOffset next
                ? "Prochaine exécution : " + FormatMoment(next)
                : "Prochaine exécution : non annoncée par Windows.";

            var last = "Dernier résultat : " + status.LastResultText;
            if (status.LastRunAt is DateTimeOffset ran)
            {
                last += " · dernière exécution le " + FormatMoment(ran);
            }

            if (!string.IsNullOrWhiteSpace(status.Status))
            {
                last += " · état Windows : " + status.Status;
            }

            ReminderLastRun.Text = last;
        }
        catch (Exception ex)
        {
            if (version != _reminderStatusVersion)
            {
                return;
            }

            ReminderTaskState.Text = "État de la tâche indisponible.";
            ReminderNextRun.Text = string.Empty;
            ReminderLastRun.Text = ex.Message;
        }
    }

    private static string FormatMoment(DateTimeOffset value)
        => value.ToLocalTime().ToString("dddd d MMMM yyyy à HH:mm", AppState.French);

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

    private async void OnAnalyze(object sender, RoutedEventArgs e) => await RunAnalysisAsync(fullRescan: false);

    private async void OnReanalyzeAll(object sender, RoutedEventArgs e) => await RunAnalysisAsync(fullRescan: true);

    /// <summary>
    /// Lance une passe d'analyse. Le mode incrémental est le comportement par défaut : seuls les
    /// souvenirs sans texte reconnu ni empreinte visuelle sont recalculés. Le rescan complet
    /// n'est proposé que par « Tout réanalyser », après une confirmation explicite.
    /// </summary>
    private async Task RunAnalysisAsync(bool fullRescan)
    {
        if (AppState.Current.Library is null)
        {
            Show("Aucune bibliothèque", "Ouvrez une bibliothèque avant de lancer l'analyse.", InfoBarSeverity.Warning);
            return;
        }
        if (_analysisCts is not null)
        {
            // Une passe est déjà en cours : un double clic très rapide ne doit pas en lancer deux.
            return;
        }
        if (fullRescan && !await ConfirmFullRescanAsync())
        {
            return;
        }

        _analysisCts = new CancellationTokenSource();
        SetAnalysisButtonsEnabled(false);
        AnalysisProgress.Visibility = Visibility.Visible;
        AnalysisProgress.Value = 0;
        AnalysisStatus.Text = fullRescan ? "Rescan complet en préparation…" : "Préparation…";
        SetAnalysisDeletionEnabled(false);

        try
        {
            var runner = new AnalysisRunner(AppState.Current.Library);
            var options = new AnalysisRunOptions
            {
                Ocr = OcrSwitch.IsOn,
                VisualHash = true,
                Faces = FacesSwitch.IsOn,
                // Le mode incrémental saute ce qui existe déjà ; le rescan complet le force.
                SkipExisting = !fullRescan
            };
            var progress = new Progress<AnalysisProgress>(p =>
            {
                AnalysisProgress.Maximum = Math.Max(1, p.Total);
                AnalysisProgress.Value = Math.Min(p.Done, p.Total);
                AnalysisStatus.Text = $"{p.Message} ({p.Done}/{p.Total})";
            });
            var summary = await runner.RunAsync(options, progress, _analysisCts.Token);
            if (summary.Cancelled)
            {
                AnalysisStatus.Text = "Analyse annulée.";
                Show(
                    "Analyse annulée",
                    "Les résultats déjà enregistrés sont conservés ; une nouvelle passe reprendra les souvenirs non traités.",
                    InfoBarSeverity.Informational);
            }
            else
            {
                AnalysisStatus.Text =
                    $"{summary.OcrCompleted} OCR · {summary.VisualHashesCompleted} empreintes · " +
                    $"{summary.FacesDetected} visages · {summary.FaceGroupsCreated} groupes" +
                    (fullRescan
                        ? " · rescan complet"
                        : $" · {summary.OcrSkipped + summary.VisualHashesSkipped} déjà à jour");
                Show(
                    fullRescan ? "Rescan complet terminé" : "Analyse terminée",
                    summary.Error ?? (fullRescan
                        ? $"Traitement local terminé en {summary.Duration.TotalSeconds:0} s. Tous les textes et empreintes ont été recalculés."
                        : $"Traitement local terminé en {summary.Duration.TotalSeconds:0} s. {summary.OcrSkipped} texte(s) et {summary.VisualHashesSkipped} empreinte(s) déjà présents ont été conservés."),
                    summary.Error is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            }
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
            // Les textes reconnus ont pu changer : la recherche des autres pages doit relire
            // le catalogue au lieu de réutiliser son cache.
            MemorySearch.InvalidateOcrCache();
            SetAnalysisButtonsEnabled(true);
            AnalysisProgress.Visibility = Visibility.Collapsed;
            await RefreshAnalysisDataAsync();
        }
    }

    /// <summary>
    /// Confirmation du seul chemin qui recalcule tout. Le coût peut être élevé sur une grande
    /// bibliothèque, et les textes déjà présents sont remplacés.
    /// </summary>
    private async Task<bool> ConfirmFullRescanAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Tout réanalyser ?",
            Content =
                "Les textes reconnus et les empreintes visuelles seront recalculés pour toutes les photos, " +
                "même celles déjà analysées. Sur une grande bibliothèque, cette opération peut prendre longtemps. " +
                "Les souvenirs, les médias, les lieux et les dates corrigées ne sont pas modifiés. " +
                "Une analyse des visages, si elle est activée, est également relancée ; les groupes nommés sont conservés.",
            PrimaryButtonText = "Tout réanalyser",
            CloseButtonText = "Annuler",
            // L'action longue et coûteuse n'est jamais le bouton par défaut.
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetAnalysisButtonsEnabled(bool enabled)
    {
        AnalyzeButton.IsEnabled = enabled;
        ReanalyzeAllButton.IsEnabled = enabled;
        CancelAnalysisButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCancelAnalysis(object sender, RoutedEventArgs e) => _analysisCts?.Cancel();

    private void SetAnalysisDeletionEnabled(bool enabled)
    {
        ClearOcrButton.IsEnabled = enabled;
        ClearVisualButton.IsEnabled = enabled;
        ClearFacesButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Reflète l'état des données d'analyse sans jamais compter ni exposer les textes reconnus :
    /// le catalogue ne fournit pas de décompte OCR, et parcourir chaque souvenir serait coûteux.
    /// </summary>
    private async Task RefreshAnalysisDataAsync()
    {
        var library = AppState.Current.Library;
        if (library is null)
        {
            AnalysisDataStats.Text = "Ouvrez une bibliothèque pour consulter les données d'analyse.";
            ClearOcrButton.IsEnabled = false;
            ClearVisualButton.IsEnabled = false;
            ClearFacesButton.IsEnabled = false;
            return;
        }

        try
        {
            var hashes = await library.Analysis.GetVisualHashesAsync();
            var faces = await library.Analysis.GetFacesAsync();
            var groups = await library.Analysis.GetPersonGroupsAsync();
            AnalysisDataStats.Text =
                $"{hashes.Count} empreinte(s) visuelle(s) · {faces.Count} visage(s) détecté(s) · {groups.Count} groupe(s) de personnes.";
            ClearOcrButton.IsEnabled = _analysisCts is null;
            ClearVisualButton.IsEnabled = _analysisCts is null && hashes.Count > 0;
            ClearFacesButton.IsEnabled = _analysisCts is null && (faces.Count > 0 || groups.Count > 0);
        }
        catch (Exception ex)
        {
            AnalysisDataStats.Text = "État des données d'analyse indisponible : " + ex.Message;
        }
    }

    private async Task<bool> ConfirmAnalysisDeleteAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = action,
            CloseButtonText = "Annuler",
            // L'action destructrice n'est jamais le bouton par défaut.
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnClearOcr(object sender, RoutedEventArgs e)
    {
        var library = AppState.Current.Library;
        if (library is null)
        {
            Show("Aucune bibliothèque", "Ouvrez une bibliothèque avant de supprimer des données d'analyse.", InfoBarSeverity.Warning);
            return;
        }

        var confirmed = await ConfirmAnalysisDeleteAsync(
            "Supprimer les textes reconnus ?",
            "Les textes issus de l'OCR local seront effacés et ne seront plus trouvés par la recherche texte. " +
            "Les souvenirs, les médias et les albums ne sont pas modifiés ; une nouvelle analyse peut recalculer les textes.",
            "Supprimer les textes");
        if (!confirmed)
        {
            return;
        }

        ClearOcrButton.IsEnabled = false;
        try
        {
            await library.Analysis.ClearOcrAsync();
            // La recherche ne doit plus proposer de souvenirs dont le texte vient d'être effacé.
            MemorySearch.InvalidateOcrCache();
            await RefreshAnalysisDataAsync();
            Show("Textes OCR supprimés",
                "Aucun souvenir n'a été supprimé. Relancez l'analyse pour recalculer les textes reconnus.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Suppression impossible", ex.Message, InfoBarSeverity.Error);
            await RefreshAnalysisDataAsync();
        }
    }

    private async void OnClearVisual(object sender, RoutedEventArgs e)
    {
        var library = AppState.Current.Library;
        if (library is null)
        {
            Show("Aucune bibliothèque", "Ouvrez une bibliothèque avant de supprimer des données d'analyse.", InfoBarSeverity.Warning);
            return;
        }

        var confirmed = await ConfirmAnalysisDeleteAsync(
            "Supprimer les empreintes visuelles ?",
            "Les empreintes calculées à partir des miniatures seront effacées et la recherche par ressemblance ne renverra plus de résultats. " +
            "Aucun souvenir, média ou album n'est supprimé.",
            "Supprimer les empreintes");
        if (!confirmed)
        {
            return;
        }

        ClearVisualButton.IsEnabled = false;
        try
        {
            await library.Analysis.ClearVisualAsync();
            await RefreshAnalysisDataAsync();
            Show("Empreintes supprimées",
                "Les souvenirs restent intacts. Relancez l'analyse pour recalculer les empreintes.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Suppression impossible", ex.Message, InfoBarSeverity.Error);
            await RefreshAnalysisDataAsync();
        }
    }

    private async void OnClearFaces(object sender, RoutedEventArgs e)
    {
        var library = AppState.Current.Library;
        if (library is null)
        {
            Show("Aucune bibliothèque", "Ouvrez une bibliothèque avant de supprimer des données d'analyse.", InfoBarSeverity.Warning);
            return;
        }

        var confirmed = await ConfirmAnalysisDeleteAsync(
            "Supprimer les visages détectés ?",
            "Les visages détectés et les groupes de personnes proposés, y compris les noms saisis, seront supprimés. " +
            "Aucun souvenir, média ou album n'est supprimé. Une nouvelle analyse recréera des groupes sans nom.",
            "Supprimer les visages");
        if (!confirmed)
        {
            return;
        }

        ClearFacesButton.IsEnabled = false;
        try
        {
            await library.Analysis.ClearFacesAsync();
            // ClearFacesAsync conserve volontairement les groupes nommés. On les supprime ici
            // pour ne pas laisser des groupes vides dans l'onglet Personnes.
            foreach (var group in await library.Analysis.GetPersonGroupsAsync())
            {
                await library.Analysis.DeletePersonGroupAsync(group.Id);
            }
            await RefreshAnalysisDataAsync();
            Show("Visages supprimés",
                "Les souvenirs restent intacts. Une nouvelle analyse peut proposer d'autres groupes, sans nom.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Suppression impossible", ex.Message, InfoBarSeverity.Error);
            await RefreshAnalysisDataAsync();
        }
    }

    private void Show(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }
}






