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

        foreach (var zone in TimeZoneInfo.GetSystemTimeZones().OrderBy(z => z.DisplayName, StringComparer.CurrentCulture))
        {
            TimeZoneBox.Items.Add(new ComboBoxItem { Content = zone.DisplayName, Tag = zone.Id });
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
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        var tag = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
        AppState.Current.Settings = AppState.Current.Settings with { Theme = tag };
        App.MainWindow?.ApplyTheme(tag);
        Show("Thème appliqué", "Le choix est conservé pour cette session.", InfoBarSeverity.Success);
    }

    private void OnMotionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        AppState.Current.Settings = AppState.Current.Settings with { ReduceMotion = MotionSwitch.IsOn };
    }

    private void OnPhotoDurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
        {
            return;
        }
        AppState.Current.Settings = AppState.Current.Settings with { PhotoDurationSeconds = (int)args.NewValue };
    }

    private void OnPlaybackChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        AppState.Current.Settings = AppState.Current.Settings with
        {
            AutoplayVideos = AutoplaySwitch.IsOn,
            PlayAudio = AudioSwitch.IsOn
        };
    }

    private void OnTimeZoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        if (TimeZoneBox.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            AppState.Current.Settings = AppState.Current.Settings with { TimeZoneId = id };
            Show("Fuseau de classement modifié",
                "Les jours seront recalculés à partir des instants conservés. Aucune date d'origine n'est modifiée.",
                InfoBarSeverity.Informational);
        }
    }

    private void OnRemindersChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        AppState.Current.Settings = AppState.Current.Settings with { RemindersEnabled = RemindersSwitch.IsOn };
    }

    private void OnReminderTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args)
    {
        if (_loading)
        {
            return;
        }
        AppState.Current.Settings = AppState.Current.Settings with
        {
            ReminderTime = (args.NewTime ?? TimeSpan.FromHours(9)).ToString(@"hh\:mm")
        };
    }

    private void OnDiscreetChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        AppState.Current.Settings = AppState.Current.Settings with { DiscreetNotifications = DiscreetSwitch.IsOn };
    }

    private void OnAnalysisChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        AppState.Current.Settings = AppState.Current.Settings with
        {
            OcrEnabled = OcrSwitch.IsOn,
            FacesEnabled = FacesSwitch.IsOn
        };
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
        Show("Ouverture non disponible",
            "L'ouverture d'une bibliothèque existante sera reliée au catalogue après l'intégration du noyau. Aucun fichier n'a été modifié.",
            InfoBarSeverity.Warning);
    }

    private async void OnCreateLibrary(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }
        Show("Création non disponible",
            "La création de bibliothèque sera reliée au catalogue après l'intégration du noyau. Aucun fichier n'a été modifié.",
            InfoBarSeverity.Warning);
    }

    private async void OnBackup(object sender, RoutedEventArgs e) => await ExplainBackupAsync("Sauvegarde");
    private async void OnRestore(object sender, RoutedEventArgs e) => await ExplainBackupAsync("Restauration");

    private async Task ExplainBackupAsync(string action)
    {
        var dialog = new ContentDialog
        {
            Title = action,
            Content = "Cette action nécessite une bibliothèque ouverte. Elle sera disponible dès qu'un catalogue local est chargé ; aucun export personnel n'est inclus dans l'application.",
            CloseButtonText = "Fermer",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
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

    private void Show(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }
}
