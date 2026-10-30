using Memo.App.Notifications;
using Microsoft.UI.Xaml;

namespace Memo.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    private MainWindow? _window;

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
        ToastService.Current.Activated += OnToastActivated;
        ToastService.Current.Register();

        try
        {
            var startup = await ReminderRunner.TryHandleStartupAsync(Environment.GetCommandLineArgs());
            if (startup.Kind == ReminderStartupKind.ReminderInvocation)
            {
                Environment.Exit(startup.Reminder?.ExitCode ?? 0);
                return;
            }

            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();

            if (startup.Kind == ReminderStartupKind.NotificationActivation && startup.Activation?.Day is DateOnly day)
            {
                _window.ShowDay(day);
            }
        }
        catch (Exception ex)
        {
            Log(ex);
            if (_window is null)
            {
                _window = new MainWindow();
                MainWindow = _window;
                _window.Activate();
            }
        }
    }

    private void OnToastActivated(object? sender, ToastActivatedEventArgs e)
    {
        MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (e.Activation.Day is DateOnly day)
            {
                MainWindow?.ShowDay(day);
            }
        });
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

