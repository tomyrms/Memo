using System.Globalization;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Memo.App.Notifications;

/// <summary>
/// Charge utile d'une notification activée par l'utilisateur. Le jour est
/// toujours exprimé en ISO 8601 (yyyy-MM-dd), jamais avec un libellé localisé.
/// </summary>
public sealed record ToastActivation(DateOnly? Day, string? LocalPath)
{
    public string? DayText => Day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed class ToastActivatedEventArgs : EventArgs
{
    public ToastActivatedEventArgs(ToastActivation activation)
    {
        Activation = activation;
    }

    public ToastActivation Activation { get; }

    /// <summary>Jour demandé, ou null si l'activation ne portait qu'un chemin.</summary>
    public DateOnly? Day => Activation.Day;

    /// <summary>Identifiant de jour au format yyyy-MM-dd, ou null.</summary>
    public string? DayText => Activation.DayText;

    /// <summary>Chemin de bibliothèque éventuel. Absent des charges utiles produites par Memo.</summary>
    public string? LocalPath => Activation.LocalPath;
}

/// <summary>
/// Notifications Windows locales via Microsoft.Windows.AppNotifications.
///
/// Aucune donnée personnelle n'est placée dans la charge utile : seuls le jour
/// (yyyy-MM-dd) et une action d'ouverture sont transmis. Le nom de la
/// bibliothèque, les lieux et les personnes ne sortent jamais de Memo.
/// L'événement <see cref="Activated"/> est levé sur le thread du callback WinRT ;
/// l'appelant doit revenir sur le thread UI avec son DispatcherQueue.
/// </summary>
public sealed class ToastService
{
    private const string AppDisplayName = "Memo";
    private const string GroupName = "memo-reminder";

    private static readonly object Gate = new();
    private static readonly object LogGate = new();
    private static readonly TimeSpan ActivationDedupeWindow = TimeSpan.FromSeconds(2);

    private bool _registered;
    private string? _lastError;
    private string? _lastActivationKey;
    private DateTimeOffset _lastActivationAt;
    private ToastActivation? _pendingActivation;

    private ToastService()
    {
    }

    public static ToastService Current { get; } = new();

    /// <summary>
    /// Levé quand l'utilisateur clique sur une notification Memo, dans le
    /// processus qui a enregistré le service. Thread non UI.
    /// </summary>
    public event EventHandler<ToastActivatedEventArgs>? Activated;

    public bool IsRegistered
    {
        get
        {
            lock (Gate)
            {
                return _registered;
            }
        }
    }

    /// <summary>Dernier diagnostic d'enregistrement ou d'affichage, en français.</summary>
    public string? LastError
    {
        get
        {
            lock (Gate)
            {
                return _lastError;
            }
        }
    }

    public static string DiagnosticLogPath => MemoLocalPaths.NotificationLog;

    /// <summary>
    /// Enregistre le processus courant comme serveur d'activation des
    /// notifications. Idempotent : les appels suivants ne font rien.
    /// </summary>
    /// <returns>Vrai si les notifications sont utilisables.</returns>
    public bool Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return true;
            }

            try
            {
                if (!AppNotificationManager.IsSupported())
                {
                    return Fail("Les notifications Windows ne sont pas prises en charge sur cette version de Windows.");
                }

                var manager = AppNotificationManager.Default;

                // L'abonnement doit précéder Register pour que le clic soit
                // traité dans ce processus quand il est déjà en cours.
                manager.NotificationInvoked -= OnNotificationInvoked;
                manager.NotificationInvoked += OnNotificationInvoked;

                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Memo.png");
                try
                {
                    if (File.Exists(iconPath))
                    {
                        // Surcharge utile pour une application non empaquetée :
                        // le nom affiché et l'icône viennent de l'exécutable.
                        manager.Register(AppDisplayName, new Uri(iconPath));
                    }
                    else
                    {
                        manager.Register();
                    }
                }
                catch (Exception ex)
                {
                    // Certaines versions ne permettent pas la surcharge : on
                    // retombe sur l'enregistrement minimal avant d'abandonner.
                    Log("Enregistrement avec icône ignoré : " + ex.Message);
                    manager.Register();
                }

                _registered = true;
                _lastError = null;
                Log("Notifications enregistrées.");
                return true;
            }
            catch (Exception ex)
            {
                return Fail("Impossible d'enregistrer les notifications Windows : " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Affiche le rappel quotidien normal : titre « Ce jour-là », nombre de
    /// souvenirs et miniature locale facultative.
    /// </summary>
    public Task<bool> ShowDailyMemoryAsync(
        DateOnly day,
        int memoryCount,
        string? thumbnailPath = null,
        CancellationToken ct = default)
        => ShowDailyMemoryAsync(day, memoryCount, discreet: false, thumbnailPath, ct);

    /// <summary>
    /// Affiche le rappel quotidien. En mode discret, aucune miniature, aucun
    /// lieu et aucun libellé personnel ne sont utilisés.
    /// </summary>
    public Task<bool> ShowDailyMemoryAsync(
        DateOnly day,
        int memoryCount,
        bool discreet,
        string? thumbnailPath = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ShowDailyMemoryCore(day, memoryCount, discreet, thumbnailPath));
    }

    private bool ShowDailyMemoryCore(DateOnly day, int memoryCount, bool discreet, string? thumbnailPath)
    {
        if (memoryCount <= 0)
        {
            return Fail("Aucun souvenir à notifier.");
        }

        if (!Register())
        {
            return false;
        }

        try
        {
            var setting = AppNotificationManager.Default.Setting;
            if (setting != AppNotificationSetting.Enabled)
            {
                return Fail("Les notifications Windows sont désactivées (" + DescribeSetting(setting) + ").");
            }

            var dayText = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "open-this-day")
                .AddArgument("day", dayText)
                .SetGroup(GroupName)
                .SetTag("d-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

            if (discreet)
            {
                // Texte volontairement neutre : ni année, ni lieu, ni nom.
                builder.AddText(AppDisplayName)
                    .AddText("Vos souvenirs sont prêts.");
            }
            else
            {
                builder.AddText("Ce jour-là")
                    .AddText(memoryCount == 1
                        ? "1 souvenir vous attend"
                        : memoryCount.ToString(CultureInfo.InvariantCulture) + " souvenirs vous attendent");

                if (!string.IsNullOrWhiteSpace(thumbnailPath) && File.Exists(thumbnailPath))
                {
                    try
                    {
                        builder.SetInlineImage(ToFileUri(thumbnailPath));
                    }
                    catch (Exception ex)
                    {
                        // Une miniature invalide ne doit pas supprimer le rappel.
                        Log("Miniature ignorée : " + ex.Message);
                    }
                }
            }

            var notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);

            lock (Gate)
            {
                _lastError = null;
            }

            Log($"Notification affichée pour {dayText} ({memoryCount} souvenir(s), discrète={discreet}).");
            return true;
        }
        catch (Exception ex)
        {
            return Fail("Impossible d'afficher la notification : " + ex.Message);
        }
    }

    /// <summary>
    /// Réessaie l'événement pour une activation déjà connue. Utilisé au
    /// démarrage de l'application quand Windows a lancé Memo par notification.
    /// </summary>
    internal void RaiseActivated(ToastActivation activation)
    {
        var key = activation.DayText ?? activation.LocalPath ?? string.Empty;
        var now = DateTimeOffset.UtcNow;

        lock (Gate)
        {
            // Windows peut livrer la même activation par deux chemins
            // (GetActivatedEventArgs et NotificationInvoked) : on déduplique.
            if (string.Equals(_lastActivationKey, key, StringComparison.Ordinal)
                && now - _lastActivationAt < ActivationDedupeWindow)
            {
                return;
            }

            _lastActivationKey = key;
            _lastActivationAt = now;
            _pendingActivation = activation;
        }

        try
        {
            Activated?.Invoke(this, new ToastActivatedEventArgs(activation));
        }
        catch (Exception ex)
        {
            // Une exception dans l'abonné ne doit pas casser le callback WinRT.
            Log("Erreur dans le gestionnaire Activated : " + ex.Message);
        }
    }

    /// <summary>
    /// Récupère une activation reçue pendant l'enregistrement, avant que la
    /// fenêtre ne soit créée. Retourne null si aucune activation n'est en
    /// attente. L'appel consomme l'activation.
    /// </summary>
    internal ToastActivation? TryTakePendingActivation()
    {
        lock (Gate)
        {
            var activation = _pendingActivation;
            _pendingActivation = null;
            return activation;
        }
    }

    /// <summary>Retire l'enregistrement du processus courant.</summary>
    public void Unregister()
    {
        lock (Gate)
        {
            if (!_registered)
            {
                return;
            }

            try
            {
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked -= OnNotificationInvoked;
                manager.Unregister();
            }
            catch (Exception ex)
            {
                Log("Désenregistrement impossible : " + ex.Message);
            }
            finally
            {
                _registered = false;
            }
        }
    }

    /// <summary>
    /// Nettoie toutes les données d'enregistrement Memo. À réserver à la
    /// désinstallation : les notifications suivantes ne fonctionneront plus
    /// avant un nouvel appel à <see cref="Register"/>.
    /// </summary>
    public void UnregisterAll()
    {
        lock (Gate)
        {
            try
            {
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked -= OnNotificationInvoked;
                manager.UnregisterAll();
            }
            catch (Exception ex)
            {
                Log("Nettoyage des notifications impossible : " + ex.Message);
            }
            finally
            {
                _registered = false;
            }
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            var activation = TryParseActivation(args.Arguments);
            if (activation is null)
            {
                Log("Activation de notification sans jour ni chemin reconnu.");
                return;
            }

            RaiseActivated(activation);
        }
        catch (Exception ex)
        {
            Log("Lecture de l'activation impossible : " + ex.Message);
        }
    }

    /// <summary>
    /// Lit les arguments d'activation. Seuls un jour ISO valide et un chemin
    /// absolu sont acceptés ; tout le reste est ignoré.
    /// </summary>
    public static ToastActivation? TryParseActivation(IDictionary<string, string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        DateOnly? day = null;
        if (arguments.TryGetValue("day", out var dayText)
            && DateOnly.TryParseExact(
                dayText,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDay))
        {
            day = parsedDay;
        }

        string? localPath = null;
        if (arguments.TryGetValue("library", out var rawPath)
            && !string.IsNullOrWhiteSpace(rawPath)
            && Path.IsPathFullyQualified(rawPath))
        {
            try
            {
                localPath = Path.GetFullPath(rawPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                localPath = null;
            }
        }

        return day is null && localPath is null ? null : new ToastActivation(day, localPath);
    }

    private static Uri ToFileUri(string path)
        => new UriBuilder
        {
            Scheme = Uri.UriSchemeFile,
            Path = Path.GetFullPath(path),
        }.Uri;

    private static string DescribeSetting(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.DisabledForApplication => "désactivées pour Memo",
        AppNotificationSetting.DisabledForUser => "désactivées pour l'utilisateur",
        AppNotificationSetting.DisabledByGroupPolicy => "bloquées par une stratégie de groupe",
        AppNotificationSetting.DisabledByManifest => "bloquées par le manifeste",
        AppNotificationSetting.Unsupported => "non prises en charge",
        _ => "réglage inconnu",
    };

    private bool Fail(string message)
    {
        lock (Gate)
        {
            _lastError = message;
        }

        Log("ERREUR : " + message);
        return false;
    }

    private static void Log(string message)
    {
        lock (LogGate)
        {
            try
            {
                Directory.CreateDirectory(MemoLocalPaths.Folder);
                var path = MemoLocalPaths.NotificationLog;
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
                // La journalisation est best-effort et ne doit jamais faire échouer un rappel.
            }
        }
    }
}
