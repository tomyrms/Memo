using Memo.App.Services;
using Memo.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI;

namespace Memo.App;

public sealed partial class MainWindow : Window
{
    private DateOnly? _pendingDay;
    private bool _shellReady;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Memo";
        Shell.Ready += OnShellReady;
        AppWindow.Resize(new SizeInt32(1320, 860));

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        AppWindow.Move(new PointInt32(
            work.X + (work.Width - 1320) / 2,
            work.Y + (work.Height - 860) / 2));

        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Memo.ico"));
        }
        catch
        {
            // icône best-effort
        }

        Root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        UpdateTitleBar();

        var pageArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        if (pageArg is not null)
        {
            Shell.StartPage = pageArg["--page=".Length..];
        }

        var argv = Environment.GetCommandLineArgs();
        var importIndex = Array.FindIndex(argv, a => a.Equals("--import", StringComparison.OrdinalIgnoreCase));
        if (importIndex >= 0)
        {
            var importPaths = argv.Skip(importIndex + 1)
                .TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))
                .ToArray();
            if (importPaths.Length > 0)
            {
                Shell.Ready += async (_, _) => await Shell.RunImportAsync(importPaths);
            }
        }

        if (argv.Any(a => a.Equals("--story-photo", StringComparison.OrdinalIgnoreCase)))
        {
            Shell.Ready += (_, _) =>
            {
                var photos = Services.AppState.Current.Cards
                    .Where(c => c.Kind == MediaKind.Photo && c.OverlayCount > 0)
                    .Take(4)
                    .ToList();
                if (photos.Count == 0)
                {
                    photos = Services.AppState.Current.Cards.Where(c => c.Kind == MediaKind.Photo).Take(4).ToList();
                }
                ShowStory(photos, 0);
            };
        }
        else if (Environment.GetCommandLineArgs().Any(a => a.Equals("--story", StringComparison.OrdinalIgnoreCase)))
        {
            Shell.Ready += (_, _) =>
            {
                var items = Services.AppState.Current.Cards
                    .Where(c => c.Kind == MediaKind.Video || c.OverlayCount > 0)
                    .OrderByDescending(c => c.LocalDate)
                    .Take(6)
                    .ToList();
                ShowStory(items, 0);
            };
        }
    }

    public void Navigate(string tag) => Shell.Navigate(tag);

    /// <summary>
    /// Ouvre « Ce jour-là » pour le jour concerné : même jour et même mois dans
    /// les années antérieures, hors souvenirs masqués et hors calques seuls.
    /// L'appel peut précéder le chargement de la bibliothèque (activation par
    /// notification) ; la sélection est alors rejouée quand la coquille est prête.
    /// </summary>
    public void ShowDay(DateOnly day)
    {
        if (!_shellReady)
        {
            _pendingDay = day;
            return;
        }

        // Le jour de la charge utile peut dater (notification conservée dans
        // le Centre de notifications). L'anniversaire se calcule par rapport à
        // l'année courante de la bibliothèque, pas à l'année de la charge utile.
        var items = AppState.Current.ThisDay(AnniversaryReference(day))
            .Where(c => c.Status != MemoryStatus.OverlayOnly)
            .OrderBy(c => c.LocalDate)
            .ThenBy(c => c.ExternalKey, StringComparer.Ordinal)
            .ToList();
        if (items.Count == 0)
        {
            Shell.Select("home");
            Navigate("home");
            return;
        }

        ShowStory(items, 0);
    }

    private static DateOnly AnniversaryReference(DateOnly requested)
    {
        // Le 29 février n'existe pas toutes les années : on cherche la
        // prochaine année bissextile pour inclure tous les anniversaires passés.
        var year = AppState.Current.Today.Year;
        while (true)
        {
            try
            {
                return new DateOnly(year, requested.Month, requested.Day);
            }
            catch (ArgumentOutOfRangeException)
            {
                year++;
            }
        }
    }

    private void OnShellReady(object? sender, EventArgs e)
    {
        _shellReady = true;
        if (_pendingDay is not DateOnly day)
        {
            return;
        }

        _pendingDay = null;
        ShowDay(day);
    }

    public void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        UpdateTitleBar();
    }

    private void UpdateTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var dark = Root.ActualTheme == ElementTheme.Dark;
        var background = dark ? Color.FromArgb(255, 20, 18, 16) : Color.FromArgb(255, 244, 241, 236);
        var foreground = dark ? Color.FromArgb(255, 244, 239, 231) : Color.FromArgb(255, 29, 26, 22);
        var muted = dark ? Color.FromArgb(255, 176, 167, 154) : Color.FromArgb(255, 95, 89, 79);
        var hover = dark ? Color.FromArgb(255, 38, 34, 32) : Color.FromArgb(255, 234, 229, 220);
        var pressed = dark ? Color.FromArgb(255, 55, 49, 43) : Color.FromArgb(255, 216, 209, 197);

        var bar = AppWindow.TitleBar;
        bar.BackgroundColor = background;
        bar.ForegroundColor = foreground;
        bar.InactiveBackgroundColor = background;
        bar.InactiveForegroundColor = muted;
        bar.ButtonBackgroundColor = background;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = pressed;
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonInactiveBackgroundColor = background;
        bar.ButtonInactiveForegroundColor = muted;
    }

    public void ShowStory(IReadOnlyList<MemoryCard> items, int index)
    {
        if (items.Count == 0)
        {
            return;
        }
        Shell.IsEnabled = false;
        Story.Visibility = Visibility.Visible;
        Story.Closed -= OnStoryClosed;
        Story.Closed += OnStoryClosed;
        Story.Start(items, Math.Clamp(index, 0, items.Count - 1));
    }

    private void OnStoryClosed(object? sender, EventArgs e)
    {
        Story.Visibility = Visibility.Collapsed;
        Shell.IsEnabled = true;
        Shell.Focus(FocusState.Programmatic);
        Story.Closed -= OnStoryClosed;
    }
}






