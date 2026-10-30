using System.Globalization;
using Memo.Core;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace Memo.App.Notifications;

public enum ReminderStartupKind
{
    /// <summary>Lancement normal : rien à traiter.</summary>
    NotHandled = 0,

    /// <summary>Memo.exe a été lancé par la tâche planifiée avec --reminder.</summary>
    ReminderInvocation = 1,

    /// <summary>Memo a été lancé par un clic sur une notification.</summary>
    NotificationActivation = 2,
}

/// <summary>
/// Résultat du traitement d'un démarrage. L'appelant décide ensuite d'afficher
/// la fenêtre ou de quitter avec le code du rappel.
/// </summary>
public sealed record ReminderStartupResult(
    ReminderStartupKind Kind,
    ReminderRunResult? Reminder,
    ToastActivation? Activation)
{
    public bool Handled => Kind != ReminderStartupKind.NotHandled;

    public static ReminderStartupResult None { get; } =
        new(ReminderStartupKind.NotHandled, null, null);

    public static ReminderStartupResult ForReminder(ReminderRunResult result) =>
        new(ReminderStartupKind.ReminderInvocation, result, null);

    public static ReminderStartupResult ForActivation(ToastActivation activation) =>
        new(ReminderStartupKind.NotificationActivation, null, activation);
}

/// <summary>Résultat d'une exécution de rappel, avec code de sortie stable.</summary>
public sealed record ReminderRunResult(
    int ExitCode,
    string Diagnostic,
    DateOnly? Day,
    int MemoryCount,
    bool NotificationShown)
{
    public bool Success => ExitCode == ReminderRunner.ExitSuccess;
}

/// <summary>Options surtout utiles aux diagnostics et aux essais manuels.</summary>
public sealed class ReminderRunOptions
{
    /// <summary>Délai global. Toujours plafonné à deux minutes par le runner.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>Chemin d'état alternatif, pour un test isolé.</summary>
    public string? StatePath { get; init; }

    /// <summary>Ignore le réglage « rappels activés ». À réserver aux essais.</summary>
    public bool IgnoreRemindersSetting { get; init; }

    /// <summary>Autorise une seconde notification le même jour. À réserver aux essais.</summary>
    public bool IgnoreAlreadyNotified { get; init; }

    /// <summary>
    /// Écrit la proposition et le jour notifié. Désactivé par le bouton de
    /// test : un essai ne doit pas consommer le rappel du jour.
    /// </summary>
    public bool PersistState { get; init; } = true;
}

/// <summary>
/// Logique exécutée par « Memo.exe --reminder &lt;bibliothèque&gt; ».
///
/// Le processus ouvre la bibliothèque, calcule « Ce jour-là » avec la politique
/// de dates de la bibliothèque, retire les exclusions, évite de répéter une
/// notification déjà envoyée le même jour, enregistre la proposition puis
/// affiche la notification. Aucun réseau n'est utilisé. Le délai global est
/// plafonné à deux minutes.
/// </summary>
public static class ReminderRunner
{
    public const int ExitSuccess = 0;
    public const int ExitInvalidArguments = 2;
    public const int ExitRemindersDisabled = 3;
    public const int ExitAlreadyNotified = 4;
    public const int ExitNoMemories = 5;
    public const int ExitLibraryError = 10;
    public const int ExitTimeout = 11;
    public const int ExitNotificationFailed = 12;

    public const string ReminderSwitch = "--reminder";
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(115);

    public static string DiagnosticLogPath => MemoLocalPaths.ReminderLog;

    /// <summary>
    /// Détecte « --reminder &lt;chemin&gt; » ou « --reminder=&lt;chemin&gt; ».
    /// Retourne true même si le chemin manque, afin que l'application ne
    /// s'ouvre pas par erreur en mode interface.
    /// </summary>
    public static bool IsReminderInvocation(string[]? args, out string? libraryRoot)
    {
        args ??= Environment.GetCommandLineArgs();
        libraryRoot = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.Equals(ReminderSwitch, StringComparison.OrdinalIgnoreCase))
            {
                libraryRoot = index + 1 < args.Length
                              && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[index + 1]
                    : string.Empty;
                return true;
            }

            if (argument.StartsWith(ReminderSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                libraryRoot = argument[(ReminderSwitch.Length + 1)..];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Point d'entrée recommandé au démarrage de l'application. Appelle
    /// <see cref="ToastService.Register"/> avant de lire une éventuelle
    /// activation de notification.
    /// </summary>
    /// <param name="raiseActivationEvent">
    /// Quand l'appelant traite lui-même <see cref="ReminderStartupResult.Activation"/>,
    /// passer false évite d'ouvrir la Story deux fois.
    /// </param>
    public static async Task<ReminderStartupResult> TryHandleStartupAsync(
        string[]? args = null,
        CancellationToken ct = default,
        bool raiseActivationEvent = true)
    {
        args ??= Environment.GetCommandLineArgs();

        if (IsReminderInvocation(args, out var libraryRoot))
        {
            var result = await RunAsync(libraryRoot ?? string.Empty, ct: ct)
                .ConfigureAwait(false);
            return ReminderStartupResult.ForReminder(result);
        }

        var activation = TryGetNotificationActivation();
        if (activation is not null)
        {
            if (raiseActivationEvent)
            {
                ToastService.Current.RaiseActivated(activation);
            }

            // L'activation est consommée dans tous les cas : elle est déjà
            // retournée à l'appelant.
            ToastService.Current.TryTakePendingActivation();
            return ReminderStartupResult.ForActivation(activation);
        }

        // Une activation peut avoir été livrée par NotificationInvoked pendant
        // Register, avant que l'appelant n'ait créé sa fenêtre.
        var pending = ToastService.Current.TryTakePendingActivation();
        if (pending is not null)
        {
            return ReminderStartupResult.ForActivation(pending);
        }

        return ReminderStartupResult.None;
    }

    /// <summary>
    /// Variante pour un appelant qui veut seulement savoir si ce processus est
    /// un rappel planifié. Retourne null si --reminder est absent.
    /// </summary>
    public static async Task<ReminderRunResult?> TryRunFromCommandLineAsync(
        string[]? args = null,
        CancellationToken ct = default)
    {
        args ??= Environment.GetCommandLineArgs();
        if (!IsReminderInvocation(args, out var libraryRoot))
        {
            return null;
        }

        return await RunAsync(libraryRoot ?? string.Empty, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Exécute un rappel pour une bibliothèque. Retourne toujours un résultat ;
    /// une erreur de bibliothèque ou de notification ne lève pas.
    /// </summary>
    public static async Task<ReminderRunResult> RunAsync(
        string libraryRoot,
        ReminderRunOptions? options = null,
        CancellationToken ct = default)
    {
        var result = await RunCoreAsync(libraryRoot, options, ct).ConfigureAwait(false);
        WriteDiagnostic(result);
        return result;
    }

    private static async Task<ReminderRunResult> RunCoreAsync(
        string libraryRoot,
        ReminderRunOptions? options,
        CancellationToken ct)
    {
        options ??= new ReminderRunOptions();

        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return Failure(
                ExitInvalidArguments,
                "Aucun chemin de bibliothèque n'a été fourni après --reminder.");
        }

        string root;
        try
        {
            root = Path.GetFullPath(libraryRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(ExitInvalidArguments, "Chemin de bibliothèque invalide : " + ex.Message);
        }

        var timeout = ClampTimeout(options.Timeout);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        var token = linked.Token;

        try
        {
            await using var library = await MemoLibrary.OpenAsync(root, token).ConfigureAwait(false);
            var settings = library.Settings.Current;

            if (!settings.RemindersEnabled && !options.IgnoreRemindersSetting)
            {
                return Failure(
                    ExitRemindersDisabled,
                    "Les rappels sont désactivés dans les réglages de cette bibliothèque.",
                    DayInLibrary(library.Info.TimeZoneId));
            }

            var day = DayInLibrary(library.Info.TimeZoneId);
            var state = new ReminderState(options.StatePath);

            if (!options.IgnoreAlreadyNotified && state.WasNotified(library.Info.Id, day))
            {
                return new ReminderRunResult(
                    ExitAlreadyNotified,
                    $"La journée {DayText(day)} a déjà été notifiée pour cette bibliothèque ; aucun doublon n'est envoyé.",
                    day,
                    0,
                    NotificationShown: false);
            }

            var thisDay = await library.Discovery.GetThisDayAsync(
                    day,
                    new ThisDayOptions(
                        IncludeUncertainDates: settings.IncludeUncertainDatesInRecaps,
                        IncludeMediaOnly: true,
                        MaxYears: 10),
                    token)
                .ConfigureAwait(false);

            // Discovery exclut déjà les souvenirs masqués ; ce filtre est une
            // seconde barrière avant toute notification.
            var items = thisDay.Items.Where(card => !card.IsExcluded).ToList();
            if (items.Count == 0)
            {
                return new ReminderRunResult(
                    ExitNoMemories,
                    $"Aucun souvenir à rappeler pour le {DayText(day)}.",
                    day,
                    0,
                    NotificationShown: false);
            }

            if (options.PersistState)
            {
                await library.Catalog.RecordSuggestionAsync(
                        items.Select(card => card.Id).ToList(),
                        token)
                    .ConfigureAwait(false);
            }

            string? thumbnail = null;
            if (!settings.DiscreetNotifications)
            {
                foreach (var card in items)
                {
                    thumbnail = library.Catalog.GetThumbnailPath(card.Id);
                    if (!string.IsNullOrWhiteSpace(thumbnail))
                    {
                        break;
                    }
                }
            }

            var shown = await ToastService.Current.ShowDailyMemoryAsync(
                    day,
                    items.Count,
                    settings.DiscreetNotifications,
                    thumbnail,
                    token)
                .ConfigureAwait(false);

            if (!shown)
            {
                return new ReminderRunResult(
                    ExitNotificationFailed,
                    "La notification n'a pas pu être affichée : "
                    + (ToastService.Current.LastError ?? "cause inconnue"),
                    day,
                    items.Count,
                    NotificationShown: false);
            }

            if (options.PersistState)
            {
                state.MarkNotified(library.Info.Id, day, DateTimeOffset.UtcNow);
            }

            return new ReminderRunResult(
                ExitSuccess,
                $"{items.Count} souvenir(s) proposé(s) pour le {DayText(day)} ; notification affichée.",
                day,
                items.Count,
                NotificationShown: true);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return Failure(
                ExitTimeout,
                "Le rappel a dépassé la limite de deux minutes et a été interrompu.");
        }
        catch (OperationCanceledException)
        {
            return Failure(ExitTimeout, "Le rappel a été annulé.");
        }
        catch (Exception ex)
        {
            return Failure(ExitLibraryError, "Impossible d'exécuter le rappel : " + ex.Message);
        }
    }

    private static ToastActivation? TryGetNotificationActivation()
    {
        try
        {
            // Register doit précéder GetActivatedEventArgs pour que Windows
            // reconnaisse le serveur d'activation du processus.
            if (!ToastService.Current.Register())
            {
                return null;
            }

            var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activated.Kind != ExtendedActivationKind.AppNotification)
            {
                return null;
            }

            if (activated.Data is not AppNotificationActivatedEventArgs notification)
            {
                return null;
            }

            return ToastService.TryParseActivation(notification.Arguments);
        }
        catch (Exception ex)
        {
            WriteLine("Activation de notification non lisible : " + ex.Message);
            return null;
        }
    }

    private static TimeSpan ClampTimeout(TimeSpan requested)
    {
        if (requested <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(100);
        }

        return requested > MaximumDuration ? MaximumDuration : requested;
    }

    private static DateOnly DayInLibrary(string timeZoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            // Un fuseau illisible ne doit pas empêcher le rappel : on retombe
            // sur le fuseau local de la machine.
            return DateOnly.FromDateTime(DateTime.Now);
        }
    }

    private static string DayText(DateOnly day) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ReminderRunResult Failure(int code, string message, DateOnly? day = null) =>
        new(code, message, day, 0, NotificationShown: false);

    private static void WriteDiagnostic(ReminderRunResult result)
    {
        var day = result.Day is null ? "-" : DayText(result.Day.Value);
        WriteLine($"code={result.ExitCode} jour={day} souvenirs={result.MemoryCount} {result.Diagnostic}");
    }

    private static void WriteLine(string message)
    {
        try
        {
            Directory.CreateDirectory(MemoLocalPaths.Folder);
            var path = MemoLocalPaths.ReminderLog;
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 512 * 1024)
            {
                File.Delete(path);
            }

            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Le diagnostic est best-effort : il ne doit jamais faire échouer un rappel.
        }
    }
}
