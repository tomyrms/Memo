using Memo.App.Services;
using System.Runtime.InteropServices;
using System.Text.Json;
using Memo.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.UI;

namespace Memo.App;

public sealed partial class MainWindow : Window
{
    private (DateOnly Day, Guid? LibraryId)? _pendingDay;
    private bool _shellReady;

    // Tailles de référence en pixels logiques ; elles sont mises à l'échelle du moniteur.
    private const int DefaultWidth = 1320;
    private const int DefaultHeight = 860;
    private const int MinimumWidth = 720;
    private const int MinimumHeight = 520;

    private static readonly JsonSerializerOptions WindowStateJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private RectInt32 _restoredBounds;
    private bool _hasRestoredBounds;

    public static MainWindow? Current { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Title = "Memo";
        Shell.Ready += OnShellReady;

        var argv = Environment.GetCommandLineArgs();
        var pageArg = argv.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));

        // Le mode --page sert aux captures et aux vérifications : il garde une taille
        // et une position prévisibles au lieu de restaurer une session utilisateur.
        PlaceWindow(restore: pageArg is null);

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

        if (pageArg is not null)
        {
            Shell.StartPage = pageArg["--page=".Length..];
        }

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
        else if (argv.Any(a => a.Equals("--story", StringComparison.OrdinalIgnoreCase)))
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
        if (pageArg is null)
        {
            AppWindow.Changed += OnAppWindowChanged;
            AppWindow.Closing += (_, _) => SaveWindowBounds();
        }

        Closed += OnClosed;
    }

    public void Navigate(string tag) => Shell.Navigate(tag);

    /// <summary>
    /// Ouvre « Ce jour-là » pour le jour concerné : même jour et même mois dans
    /// les années antérieures, hors souvenirs masqués et hors calques seuls.
    /// L'appel peut précéder le chargement de la bibliothèque (activation par
    /// notification) ; la sélection est alors rejouée quand la coquille est prête.
    /// Si un identifiant est fourni, la bibliothèque courante doit correspondre :
    /// sinon elle est rouverte et entièrement rechargée avant le calcul.
    /// </summary>
    public void ShowDay(DateOnly day, Guid? libraryId = null)
    {
        if (!_shellReady)
        {
            _pendingDay = (day, libraryId);
            return;
        }

        if (libraryId is Guid expected && AppState.Current.LibraryId != expected)
        {
            _ = ReopenLibraryAndShowDayAsync(day, expected);
            return;
        }

        // L'activation est satisfaite : la coquille a fini de charger la bonne
        // bibliothèque, la priorité sur les ouvertures automatiques peut
        // retomber.
        AppState.Current.CompleteActivation();

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
        if (_pendingDay is not { } pending)
        {
            return;
        }

        _pendingDay = null;
        ShowDay(pending.Day, pending.LibraryId);
    }

    private async Task ReopenLibraryAndShowDayAsync(DateOnly day, Guid libraryId)
    {
        try
        {
            await AppState.Current.EnsureActivationLibraryAsync(libraryId, null);
        }
        catch (Exception ex)
        {
            AppState.Current.CompleteActivation();
            ShowLibraryNotice("Bibliothèque introuvable", ex.Message);
            return;
        }

        ShowDay(day, libraryId);
    }

    /// <summary>
    /// Affiche un message d'erreur d'activation sans bloquer le démarrage : la
    /// fenêtre peut ne pas encore avoir de XamlRoot au moment de l'appel.
    /// </summary>
    public void ShowLibraryNotice(string title, string message)
    {
        _ = ShowLibraryNoticeAsync(title, message);
    }

    private async Task ShowLibraryNoticeAsync(string title, string message)
    {
        try
        {
            if (Root.XamlRoot is null)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLoaded(object? sender, RoutedEventArgs e)
                {
                    Root.Loaded -= OnLoaded;
                    loaded.TrySetResult();
                }

                Root.Loaded += OnLoaded;
                await loaded.Task;
            }

            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Fermer",
                XamlRoot = Root.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
        }
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

    /// <summary>
    /// Place la fenêtre à sa taille de référence, ou restaure la dernière position
    /// enregistrée si elle reste visible sur un écran connecté. Dans les deux cas,
    /// la géométrie est ramenée dans la zone utile du moniteur.
    /// </summary>
    private void PlaceWindow(bool restore)
    {
        var scale = GetScale();
        var displays = DisplayArea.FindAll();
        var primary = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = primary.WorkArea;

        // La taille minimale doit tenir sur le plus petit écran connecté, sinon la
        // fenêtre ne pourrait pas être ramenée entièrement dans sa zone utile.
        var smallestWidth = displays.Count > 0 ? displays.Min(area => area.WorkArea.Width) : work.Width;
        var smallestHeight = displays.Count > 0 ? displays.Min(area => area.WorkArea.Height) : work.Height;
        var minWidth = Math.Min(Math.Max(320, (int)Math.Round(MinimumWidth * scale)), smallestWidth);
        var minHeight = Math.Min(Math.Max(240, (int)Math.Round(MinimumHeight * scale)), smallestHeight);
        ApplyMinimumSize(minWidth, minHeight);

        if (restore && LoadWindowBounds() is { } saved)
        {
            var area = DisplayAreaFor(saved);
            if (area is not null)
            {
                ApplyBounds(ClampToWorkArea(saved, area.WorkArea, minWidth, minHeight));
                return;
            }
        }

        var width = Math.Clamp((int)Math.Round(DefaultWidth * scale), minWidth, work.Width);
        var height = Math.Clamp((int)Math.Round(DefaultHeight * scale), minHeight, work.Height);
        ApplyBounds(new RectInt32(
            work.X + Math.Max(0, (work.Width - width) / 2),
            work.Y + Math.Max(0, (work.Height - height) / 2),
            width,
            height));
    }

    private void ApplyMinimumSize(int width, int height)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        try
        {
            presenter.PreferredMinimumWidth = width;
            presenter.PreferredMinimumHeight = height;
        }
        catch
        {
            // certains présentateurs ne prennent pas en charge une taille minimale
        }
    }

    private void ApplyBounds(RectInt32 bounds)
    {
        AppWindow.Resize(new SizeInt32(bounds.Width, bounds.Height));
        AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
        _restoredBounds = bounds;
        _hasRestoredBounds = true;
    }

    /// <summary>
    /// Retourne le moniteur dont la zone utile contient le plus la fenêtre, à condition
    /// que sa barre de titre reste attrapable. Un écran débranché renvoie null.
    /// </summary>
    private static DisplayArea? DisplayAreaFor(RectInt32 bounds)
    {
        DisplayArea? best = null;
        long bestOverlap = 0;
        foreach (var area in DisplayArea.FindAll())
        {
            var work = area.WorkArea;
            var left = Math.Max(bounds.X, work.X);
            var top = Math.Max(bounds.Y, work.Y);
            var right = (long)Math.Min((long)bounds.X + bounds.Width, (long)work.X + work.Width);
            var bottom = (long)Math.Min((long)bounds.Y + bounds.Height, (long)work.Y + work.Height);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            // Le haut de la fenêtre doit rester visible pour pouvoir la déplacer.
            var titleVisible = bounds.Y < (long)work.Y + work.Height
                && (long)bounds.Y + 32 > work.Y;
            if (!titleVisible || right - left < 160)
            {
                continue;
            }

            var overlap = (right - left) * (bottom - top);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = area;
            }
        }
        return best;
    }

    private static RectInt32 ClampToWorkArea(RectInt32 bounds, RectInt32 work, int minWidth, int minHeight)
    {
        var width = Math.Clamp(bounds.Width, minWidth, work.Width);
        var height = Math.Clamp(bounds.Height, minHeight, work.Height);
        return new RectInt32(
            Math.Clamp(bounds.X, work.X, work.X + work.Width - width),
            Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - height),
            width,
            height);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Une fenêtre maximisée ou réduite garde en mémoire sa dernière géométrie restaurée.
        if (sender.Presenter is OverlappedPresenter presenter
            && presenter.State != OverlappedPresenterState.Restored)
        {
            return;
        }
        if (!args.DidPositionChange && !args.DidSizeChange)
        {
            return;
        }

        var position = sender.Position;
        var size = sender.Size;
        _restoredBounds = new RectInt32(position.X, position.Y, size.Width, size.Height);
        _hasRestoredBounds = true;
    }

    private void SaveWindowBounds()
    {
        if (!_hasRestoredBounds)
        {
            return;
        }

        try
        {
            var file = WindowStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var placement = new WindowPlacement
            {
                X = _restoredBounds.X,
                Y = _restoredBounds.Y,
                Width = _restoredBounds.Width,
                Height = _restoredBounds.Height
            };
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(placement, WindowStateJson));
            File.Move(temporary, file, overwrite: true);
        }
        catch
        {
            // Mémorisation best-effort : l'application reste utilisable sans ce fichier.
        }
    }

    private static RectInt32? LoadWindowBounds()
    {
        try
        {
            var file = WindowStatePath;
            if (!File.Exists(file))
            {
                return null;
            }

            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(file), WindowStateJson);
            if (placement is null
                || placement.Width is < 200 or > 20000
                || placement.Height is < 200 or > 20000
                || Math.Abs((long)placement.X) > 100000
                || Math.Abs((long)placement.Y) > 100000)
            {
                return null;
            }
            return new RectInt32(placement.X, placement.Y, placement.Width, placement.Height);
        }
        catch
        {
            // Un fichier absent, illisible ou corrompu retombe sur le placement par défaut.
            return null;
        }
    }

    private double GetScale()
    {
        try
        {
            var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            return dpi is > 0 and <= 768 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Une activation en attente ne doit pas survivre à la fenêtre.
        AppState.Current.CompleteActivation();
        Current = null;
    }

    private static string WindowStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Memo",
        "window.json");

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private sealed class WindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
