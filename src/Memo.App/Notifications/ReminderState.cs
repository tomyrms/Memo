using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memo.App.Notifications;

/// <summary>
/// Chemins locaux utilisés par les rappels. Aucun de ces fichiers n'est envoyé
/// sur le réseau : ils restent dans le profil de l'utilisateur courant.
/// </summary>
internal static class MemoLocalPaths
{
    public static string Folder { get; } = Path.Combine(
        string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            ? AppContext.BaseDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Memo");

    public static string RemindersFile => Path.Combine(Folder, "reminders.json");
    public static string ReminderLog => Path.Combine(Folder, "reminders.log");
    public static string NotificationLog => Path.Combine(Folder, "notifications.log");
}

/// <summary>
/// Entrée conservée pour une bibliothèque : dernier jour notifié et instant de
/// la notification. Les dates sont stockées en texte pour rester lisibles et
/// tolérer une évolution du format.
/// </summary>
public sealed class ReminderLibraryEntry
{
    public string? LibraryId { get; set; }
    public string? LastNotifiedDay { get; set; }
    public string? LastNotifiedAt { get; set; }

    [JsonIgnore]
    public DateOnly? Day => DateOnly.TryParseExact(
        LastNotifiedDay,
        "yyyy-MM-dd",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out var day)
        ? day
        : null;

    [JsonIgnore]
    public DateTimeOffset? At => DateTimeOffset.TryParse(
        LastNotifiedAt,
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind,
        out var at)
        ? at
        : null;
}

internal sealed class ReminderStateDocument
{
    public int Version { get; set; } = ReminderState.CurrentVersion;
    public Dictionary<string, ReminderLibraryEntry?> Libraries { get; set; } = new();
}

/// <summary>
/// Petit état local des rappels, dans %LOCALAPPDATA%\Memo\reminders.json.
///
/// Le fichier est facultatif : son absence, un JSON corrompu ou un accès refusé
/// sont traités comme un état vide. L'écriture passe par un fichier temporaire
/// puis un remplacement atomique, afin qu'une coupure ne laisse jamais un JSON
/// tronqué. Un mutex local sérialise les lectures-modifications-écritures entre
/// le processus principal et le processus lancé par le Planificateur de tâches.
/// </summary>
public sealed class ReminderState
{
    public const int CurrentVersion = 1;

    private const string MutexName = @"Local\Memo.Reminders.State";
    private const string DayFormat = "yyyy-MM-dd";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public ReminderState(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? DefaultPath
            : Path.GetFullPath(path);
    }

    /// <summary>Emplacement par défaut : %LOCALAPPDATA%\Memo\reminders.json.</summary>
    public static string DefaultPath => MemoLocalPaths.RemindersFile;

    public string FilePath => _path;

    /// <summary>Retourne l'entrée de la bibliothèque, ou null si elle n'existe pas.</summary>
    public ReminderLibraryEntry? Get(Guid libraryId)
    {
        using var guard = AcquireMutex();
        var document = ReadCore();
        return document.Libraries.TryGetValue(Key(libraryId), out var entry) ? entry : null;
    }

    /// <summary>Vrai si ce jour a déjà été notifié pour cette bibliothèque.</summary>
    public bool WasNotified(Guid libraryId, DateOnly day)
    {
        var entry = Get(libraryId);
        return entry?.Day == day;
    }

    /// <summary>Enregistre le jour notifié, avec la date UTC de la notification.</summary>
    public void MarkNotified(Guid libraryId, DateOnly day, DateTimeOffset? notifiedAt = null)
    {
        using var guard = AcquireMutex();
        var document = ReadCore();
        var key = Key(libraryId);
        document.Libraries[key] = new ReminderLibraryEntry
        {
            LibraryId = key,
            LastNotifiedDay = day.ToString(DayFormat, CultureInfo.InvariantCulture),
            LastNotifiedAt = (notifiedAt ?? DateTimeOffset.UtcNow)
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture),
        };
        WriteCore(document);
    }

    /// <summary>Oublie l'état d'une bibliothèque (par exemple après désactivation).</summary>
    public void Clear(Guid libraryId)
    {
        using var guard = AcquireMutex();
        var document = ReadCore();
        if (document.Libraries.Remove(Key(libraryId)))
        {
            WriteCore(document);
        }
    }

    /// <summary>Supprime tout le fichier d'état. Utilisé par la désinstallation.</summary>
    public void ClearAll()
    {
        using var guard = AcquireMutex();
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Key(Guid libraryId) => libraryId.ToString("D");

    private ReminderStateDocument ReadCore()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new ReminderStateDocument();
            }

            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096,
                FileOptions.SequentialScan);
            var document = JsonSerializer.Deserialize<ReminderStateDocument>(stream, JsonOptions);
            if (document is null)
            {
                return new ReminderStateDocument();
            }

            document.Libraries ??= new Dictionary<string, ReminderLibraryEntry?>();

            // Les clés qui ne sont pas des GUID et les entrées nulles sont ignorées.
            foreach (var key in document.Libraries.Keys.ToList())
            {
                if (!Guid.TryParse(key, out _) || document.Libraries[key] is null)
                {
                    document.Libraries.Remove(key);
                }
            }

            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Un état illisible ne doit jamais empêcher un rappel : on repart vide.
            return new ReminderStateDocument();
        }
    }

    private void WriteCore(ReminderStateDocument document)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(document, JsonOptions);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // L'état est une optimisation : une écriture impossible ne doit pas
            // faire échouer l'affichage de la notification.
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static MutexGuard AcquireMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            // Le processus précédent s'est arrêté en tenant le mutex : on continue.
            acquired = true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new MutexGuard(mutex, acquired);
    }

    private readonly struct MutexGuard : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly bool _acquired;

        public MutexGuard(Mutex mutex, bool acquired)
        {
            _mutex = mutex;
            _acquired = acquired;
        }

        public void Dispose()
        {
            if (_acquired)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }

            _mutex.Dispose();
        }
    }
}
