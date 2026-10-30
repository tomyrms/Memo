using System.Globalization;
using Memo.App.Notifications;
using Memo.App.Services;
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

    private readonly SemaphoreSlim _activationGate = new(1, 1);

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

            var activation = startup.Kind == ReminderStartupKind.NotificationActivation
                ? startup.Activation
                : null;
            string? activationError = null;

            if (activation is not null)
            {
                // La cible est posée avant la création de la fenêtre pour que
                // le chargement initial de la coquille ne rouvre pas une autre
                // bibliothèque. L'ouverture et le chargement complet sont
                // attendus ici : « Ce jour-là » ne sera calculé qu'ensuite.
                AppState.Current.BeginActivation(activation.LibraryId, activation.LocalPath);
                try
                {
                    await AppState.Current.EnsureActivationLibraryAsync(
                        activation.LibraryId,
                        activation.LocalPath);
                }
                catch (Exception ex)
                {
                    activationError = ex.Message;
                    Log(ex);
                    AppState.Current.CompleteActivation();
                }
            }

            var window = EnsureWindow();
            window.Activate();

            if (activationError is not null)
            {
                window.ShowLibraryNotice("Bibliothèque introuvable", activationError);
            }
            else if (activation?.Day is DateOnly day)
            {
                ShowDayOnce(window, day, activation.LibraryId);
            }
            else if (activation is not null)
            {
                // Activation sans jour : la bibliothèque est déjà ouverte.
                AppState.Current.CompleteActivation();
            }
        }
        catch (Exception ex)
        {
            Log(ex);
            AppState.Current.CompleteActivation();
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
        var activation = e.Activation;
        Dispatch(() => _ = HandleActivationAsync(activation));
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
            if (activation is not null)
            {
                Dispatch(() => _ = HandleActivationAsync(activation));
                return;
            }
        }

        Dispatch(() => EnsureWindow().Activate());
    }

    /// <summary>
    /// Ouvre la bibliothèque visée par la notification, attend son chargement
    /// complet, puis seulement calcule et affiche « Ce jour-là ».
    /// </summary>
    private async Task HandleActivationAsync(ToastActivation activation)
    {
        await _activationGate.WaitAsync();
        try
        {
            var window = EnsureWindow();
            window.Activate();

            AppState.Current.BeginActivation(activation.LibraryId, activation.LocalPath);
            try
            {
                await AppState.Current.EnsureActivationLibraryAsync(
                    activation.LibraryId,
                    activation.LocalPath);
            }
            catch (Exception ex)
            {
                Log(ex);
                AppState.Current.CompleteActivation();
                window.ShowLibraryNotice("Bibliothèque introuvable", ex.Message);
                return;
            }

            if (activation.Day is DateOnly day)
            {
                ShowDayOnce(window, day, activation.LibraryId);
            }
            else
            {
                AppState.Current.CompleteActivation();
            }
        }
        finally
        {
            _activationGate.Release();
        }
    }

    private void ShowDayOnce(MainWindow window, DateOnly day, Guid? libraryId)
    {
        var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                  + "|" + (libraryId?.ToString("D", CultureInfo.InvariantCulture) ?? "-");
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(_lastActivationKey, key, StringComparison.Ordinal)
            && now - _lastActivationAt < ActivationDedupeWindow)
        {
            return;
        }

        _lastActivationKey = key;
        _lastActivationAt = now;
        window.Activate();
        window.ShowDay(day, libraryId);
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

    internal static void LogException(Exception? ex) => Log(ex);

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
