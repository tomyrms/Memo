using System.Globalization;
using Memo.App.Notifications;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace Memo.App;

public partial class App : Application
{
    /// <summary>Clé d'instance unique. Un second lancement transmet son activation.</summary>
    private const string InstanceKey = "Memo.Main";

    private static readonly TimeSpan ActivationDedupeWindow = TimeSpan.FromSeconds(3);

    public static MainWindow? MainWindow { get; private set; }

    private MainWindow? _window;
    private AppInstance? _instance;
    private DispatcherQueue? _dispatcher;
    private string? _lastActivationKey;
    private DateTimeOffset _lastActivationAt;

    public App()
    {
        UnhandledException += (_, e) =>
        {
            Log(e.Exception);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            Log(ex);
            throw;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        var argv = Environment.GetCommandLineArgs();

        // Rappel planifié : processus court et silencieux. Il ne rejoint jamais
        // l'instance interactive et ne crée aucune fenêtre.
        if (ReminderRunner.IsReminderInvocation(argv, out var libraryRoot))
        {
            var exitCode = ReminderRunner.ExitLibraryError;
            try
            {
                var reminder = await ReminderRunner.RunAsync(libraryRoot ?? string.Empty);
                exitCode = reminder.ExitCode;
            }
            catch (Exception ex)
            {
                Log(ex);
            }

            Environment.Exit(exitCode);
            return;
        }

        // Instance unique : un lancement ou un clic de notification pendant que
        // Memo est déjà ouvert est transmis à la première instance, qui ramène
        // sa fenêtre et ouvre le jour demandé. Si AppLifecycle est indisponible,
        // on retombe sur le comportement multi-instance.
        try
        {
            _instance = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (!_instance.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                await _instance.RedirectActivationToAsync(activation);
                Environment.Exit(0);
                return;
            }

            _instance.Activated += OnInstanceActivated;
        }
        catch (Exception ex)
        {
            Log(ex);
        }

        ToastService.Current.Activated += OnToastActivated;
        ToastService.Current.Register();

        try
        {
            // L'activation de démarrage est traitée ici : ne pas lever aussi
            // l'événement, sinon la Story serait ouverte deux fois.
            var startup = await ReminderRunner.TryHandleStartupAsync(
                argv,
                raiseActivationEvent: false);

            var window = EnsureWindow();
            window.Activate();

            if (startup.Kind == ReminderStartupKind.NotificationActivation
                && startup.Activation?.Day is DateOnly day)
            {
                ShowDayOnce(window, day);
            }
        }
        catch (Exception ex)
        {
            Log(ex);
            EnsureWindow().Activate();
        }
    }

    private MainWindow EnsureWindow()
    {
        if (_window is not null)
        {
            return _window;
        }

        var window = new MainWindow();
        _window = window;
        MainWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }

            if (ReferenceEquals(MainWindow, window))
            {
                MainWindow = null;
            }
        };
        return window;
    }

    /// <summary>
    /// Clic sur une notification Memo dans le processus déjà en cours.
    /// L'événement arrive sur le thread WinRT : tout est ramené sur le thread UI.
    /// </summary>
    private void OnToastActivated(object? sender, ToastActivatedEventArgs e)
    {
        if (e.Activation.Day is not DateOnly day)
        {
            // Charge utile sans jour : ne pas laisser le clic sans effet.
            Dispatch(() => EnsureWindow().Activate());
            return;
        }

        Dispatch(() => ShowDayOnce(EnsureWindow(), day));
    }

    /// <summary>
    /// Activation redirigée par une seconde instance (lancement normal ou clic
    /// sur une notification alors que Memo tourne déjà).
    /// </summary>
    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.AppNotification
            && args.Data is AppNotificationActivatedEventArgs notification)
        {
            var activation = ToastService.TryParseActivation(notification.Arguments);
            if (activation?.Day is DateOnly day)
            {
                Dispatch(() => ShowDayOnce(EnsureWindow(), day));
                return;
            }
        }

        Dispatch(() => EnsureWindow().Activate());
    }

    private void ShowDayOnce(MainWindow window, DateOnly day)
    {
        var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(_lastActivationKey, key, StringComparison.Ordinal)
            && now - _lastActivationAt < ActivationDedupeWindow)
        {
            return;
        }

        _lastActivationKey = key;
        _lastActivationAt = now;
        window.Activate();
        window.ShowDay(day);
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher is not null && _dispatcher.TryEnqueue(() => action()))
        {
            return;
        }

        Log(new InvalidOperationException(
            "Activation de notification ignorée : file d'attente UI indisponible."));
    }

    private static void Log(Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "memo-crash.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}]\n{ex}\n\n");
        }
        catch
        {
            // journalisation best-effort
        }
    }
}
