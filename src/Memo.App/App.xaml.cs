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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log(ex);
            throw;
        }
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
