using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Memo.App.Notifications;

/// <summary>Résultat d'une activation ou d'une désactivation de rappel.</summary>
public sealed record ReminderScheduleResult(
    bool Success,
    string TaskName,
    string Message,
    int? ExitCode = null,
    string? StandardOutput = null,
    string? StandardError = null,
    ReminderScheduleStatus? Status = null);

/// <summary>État observé d'une tâche planifiée Memo.</summary>
public sealed record ReminderScheduleStatus(
    string TaskName,
    bool Exists,
    string? Status,
    string? ScheduleType,
    TimeOnly? StartTime,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    int? LastResult,
    string? Command,
    string? RunAs,
    string? Error,
    string? RawOutput)
{
    public string LastResultText => ReminderScheduler.DescribeResult(LastResult);

    public static ReminderScheduleStatus Missing(string taskName, string? error, string? rawOutput)
        => new(taskName, false, null, null, null, null, null, null, null, null, error, rawOutput);
}

/// <summary>
/// Planification quotidienne facultative sans service Windows tiers.
///
/// La tâche est créée avec schtasks.exe dans le dossier racine du Planificateur,
/// au nom de l'utilisateur courant, avec le niveau d'exécution « Limited ».
/// Aucun mot de passe n'est demandé ni stocké et aucune élévation n'est
/// nécessaire. La tâche ne se déclenche que si Windows est allumé et si
/// l'utilisateur est connecté ; après une veille, Windows peut la lancer en
/// retard ou l'ignorer selon son état d'alimentation.
/// </summary>
public static class ReminderScheduler
{
    public const string TaskPrefix = "Memo.Reminder.";

    private const string Schtasks = "schtasks.exe";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly object EncodingGate = new();
    private static bool _encodingReady;

    /// <summary>Nom stable de la tâche pour une bibliothèque.</summary>
    public static string GetTaskName(Guid libraryId)
        => TaskPrefix + libraryId.ToString("D");

    /// <summary>
    /// Ligne de commande stockée dans la tâche. Les deux chemins sont mis entre
    /// guillemets et un antislash final est doublé pour rester valide.
    /// </summary>
    public static string BuildTaskCommand(string exePath, string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("Le chemin de Memo.exe est obligatoire.", nameof(exePath));
        }

        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            throw new ArgumentException("Le chemin de la bibliothèque est obligatoire.", nameof(libraryRoot));
        }

        return Quote(Path.GetFullPath(exePath))
               + " --reminder "
               + Quote(Path.GetFullPath(libraryRoot));
    }

    /// <summary>
    /// Active le rappel quotidien à l'heure indiquée. Accepte « HH:mm » et
    /// retourne un échec explicite si le format est invalide.
    /// </summary>
    public static async Task<ReminderScheduleResult> EnableAsync(
        string exePath,
        string libraryRoot,
        string time,
        CancellationToken ct = default)
    {
        if (!TimeOnly.TryParseExact(
                time?.Trim(),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return new ReminderScheduleResult(
                false,
                string.Empty,
                "L'heure du rappel doit être au format HH:mm, par exemple 09:00.");
        }

        return await EnableAsync(exePath, libraryRoot, parsed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Active le rappel quotidien à l'heure indiquée. La tâche est recréée si
    /// elle existe déjà (/F), ce qui rend l'opération idempotente.
    /// </summary>
    public static async Task<ReminderScheduleResult> EnableAsync(
        string exePath,
        string libraryRoot,
        TimeOnly time,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string exe;
        string root;
        try
        {
            exe = Path.GetFullPath(exePath);
            root = Path.GetFullPath(libraryRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ReminderScheduleResult(false, string.Empty, "Chemin invalide : " + ex.Message);
        }

        if (!File.Exists(exe))
        {
            return new ReminderScheduleResult(
                false,
                string.Empty,
                $"Memo.exe est introuvable : « {exe} ». Le rappel n'a pas été activé.");
        }

        if (!Directory.Exists(root))
        {
            return new ReminderScheduleResult(
                false,
                string.Empty,
                $"Le dossier de bibliothèque est introuvable : « {root} ». Le rappel n'a pas été activé.");
        }

        var libraryId = await ReadLibraryIdAsync(root, ct).ConfigureAwait(false);
        if (libraryId is null)
        {
            return new ReminderScheduleResult(
                false,
                string.Empty,
                $"Aucune bibliothèque Memo lisible dans « {root} » (library.json absent, illisible ou sans identifiant).");
        }

        var taskName = GetTaskName(libraryId.Value);
        var command = BuildTaskCommand(exe, root);
        var start = time.ToString("HH:mm", CultureInfo.InvariantCulture);

        var arguments = new List<string>
        {
            "/Create",
            "/SC", "DAILY",
            "/TN", taskName,
            "/TR", command,
            "/ST", start,
            // Garantit que la tâche ne s'exécute pas avec un jeton élevé.
            "/RL", "LIMITED",
            "/F",
        };

        var process = await RunSchtasksAsync(arguments, ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return new ReminderScheduleResult(
                false,
                taskName,
                "Windows n'a pas pu créer la tâche planifiée. " + CleanMessage(process),
                process.ExitCode,
                process.StandardOutput,
                process.StandardError);
        }

        var status = await GetStatusAsync(libraryId.Value, ct).ConfigureAwait(false);
        return new ReminderScheduleResult(
            true,
            taskName,
            $"Rappel quotidien activé à {start}. La tâche ne s'exécute que si Windows est allumé et l'utilisateur connecté.",
            process.ExitCode,
            process.StandardOutput,
            process.StandardError,
            status);
    }

    /// <summary>
    /// Supprime la tâche de rappel. L'opération est idempotente : une tâche
    /// déjà absente est considérée comme désactivée.
    /// </summary>
    public static async Task<ReminderScheduleResult> DisableAsync(
        Guid libraryId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var taskName = GetTaskName(libraryId);
        var process = await RunSchtasksAsync(
            new[] { "/Delete", "/TN", taskName, "/F" },
            ct).ConfigureAwait(false);

        if (process.ExitCode == 0)
        {
            return new ReminderScheduleResult(
                true,
                taskName,
                "Rappel désactivé : la tâche planifiée a été supprimée.",
                process.ExitCode,
                process.StandardOutput,
                process.StandardError);
        }

        if (LooksLikeMissingTask(process))
        {
            return new ReminderScheduleResult(
                true,
                taskName,
                "Rappel déjà désactivé : aucune tâche planifiée à supprimer.",
                process.ExitCode,
                process.StandardOutput,
                process.StandardError);
        }

        return new ReminderScheduleResult(
            false,
            taskName,
            "Windows n'a pas pu supprimer la tâche planifiée. " + CleanMessage(process),
            process.ExitCode,
            process.StandardOutput,
            process.StandardError);
    }

    /// <summary>Vrai si la tâche existe. Ne lève pas si schtasks est indisponible.</summary>
    public static async Task<bool> IsEnabledAsync(Guid libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var process = await RunSchtasksAsync(
            new[] { "/Query", "/TN", GetTaskName(libraryId) },
            ct).ConfigureAwait(false);
        return process.ExitCode == 0;
    }

    /// <summary>
    /// Retourne l'état détaillé : présence, heure de début, prochaine
    /// exécution, dernière exécution et dernier résultat. Une tâche absente
    /// donne Exists = false avec un message explicite.
    /// </summary>
    public static async Task<ReminderScheduleStatus> GetStatusAsync(
        Guid libraryId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var taskName = GetTaskName(libraryId);
        var process = await RunSchtasksAsync(
            new[] { "/Query", "/TN", taskName, "/V", "/FO", "LIST" },
            ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var missing = LooksLikeMissingTask(process);
            return ReminderScheduleStatus.Missing(
                taskName,
                missing ? null : CleanMessage(process),
                process.StandardOutput + Environment.NewLine + process.StandardError);
        }

        var fields = ParseList(process.StandardOutput);
        var status = Find(fields, "Statut", "Status");
        var scheduleType = Find(fields, "Type de planification", "Schedule Type");
        var startText = Find(fields, "Heure de début", "Start Time");
        var nextText = Find(fields, "Prochaine exécution", "Next Run Time");
        var lastText = Find(fields, "Heure de la dernière exécution", "Dernière exécution", "Last Run Time");
        var resultText = Find(fields, "Dernier résultat", "Last Result");
        var command = Find(fields, "Tâche à exécuter", "Task To Run");
        var runAs = Find(fields, "Exécuter en tant qu'utilisateur", "Run As User");

        TimeOnly? startTime = null;
        if (TryParseTime(startText, out var parsedStart))
        {
            startTime = parsedStart;
        }

        int? lastResult = null;
        if (int.TryParse(resultText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedResult))
        {
            lastResult = parsedResult;
        }

        return new ReminderScheduleStatus(
            taskName,
            true,
            status,
            scheduleType,
            startTime,
            ParseDateTime(nextText),
            ParseLastRun(lastText),
            lastResult,
            command,
            runAs,
            null,
            process.StandardOutput);
    }

    /// <summary>Libellé français d'un code de résultat du Planificateur.</summary>
    public static string DescribeResult(int? result) => result switch
    {
        null => "Inconnu",
        0 => "Succès",
        267008 => "Prête",
        267009 => "En cours d'exécution",
        267010 => "Désactivée",
        267011 => "Jamais exécutée",
        267012 => "Aucune exécution planifiée",
        267014 => "Interrompue",
        _ => "Code " + result.Value.ToString(CultureInfo.InvariantCulture),
    };

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private static async Task<Guid?> ReadLibraryIdAsync(string libraryRoot, CancellationToken ct)
    {
        var path = Path.Combine(libraryRoot, "library.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("format", out var format)
                && !string.Equals(format.GetString(), "memo-library", StringComparison.Ordinal))
            {
                return null;
            }

            foreach (var name in new[] { "id", "Id" })
            {
                if (root.TryGetProperty(name, out var idElement)
                    && Guid.TryParse(idElement.GetString(), out var id))
                {
                    return id;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }

        return null;
    }

    private static async Task<ProcessResult> RunSchtasksAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var encoding = GetConsoleEncoding();
        var startInfo = new ProcessStartInfo
        {
            FileName = Schtasks,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, "schtasks.exe n'a pas pu démarrer.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(
                -1,
                string.Empty,
                "L'outil schtasks.exe est introuvable ou inaccessible : " + ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Le processus peut déjà être terminé.
            }

            var reason = ct.IsCancellationRequested
                ? "Opération annulée."
                : "schtasks.exe n'a pas répondu dans le délai imparti.";
            return new ProcessResult(-3, string.Empty, reason);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static Encoding GetConsoleEncoding()
    {
        lock (EncodingGate)
        {
            if (!_encodingReady)
            {
                try
                {
                    Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                }
                catch
                {
                    // Le fournisseur est déjà enregistré ou indisponible.
                }

                _encodingReady = true;
            }
        }

        try
        {
            // schtasks écrit avec la page de code OEM de la console ; sans
            // cela, les libellés accentués seraient illisibles.
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        if (escaped.EndsWith('\\'))
        {
            // Un antislash final doublerait le guillemet fermant.
            escaped += "\\";
        }

        return "\"" + escaped + "\"";
    }

    private static Dictionary<string, string> ParseList(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0)
            {
                fields[key] = value;
            }
        }

        return fields;
    }

    private static string? Find(Dictionary<string, string> fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (fields.TryGetValue(name, out var value))
            {
                return value;
            }
        }

        // Repli insensible aux accents et à la casse, pour les systèmes dont
        // les libellés diffèrent légèrement.
        foreach (var pair in fields)
        {
            var normalizedKey = Normalize(pair.Key);
            foreach (var name in names)
            {
                if (string.Equals(normalizedKey, Normalize(name), StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }
        }

        return null;
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString().Trim();
    }

    private static bool TryParseTime(string? value, out TimeOnly time)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && TimeOnly.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out time))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(value)
            && TimeOnly.TryParseExact(
                value,
                "HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out time))
        {
            return true;
        }

        time = default;
        return false;
    }

    private static DateTimeOffset? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var local))
        {
            return new DateTimeOffset(local);
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
        {
            return new DateTimeOffset(local);
        }

        return null;
    }

    private static DateTimeOffset? ParseLastRun(string? value)
    {
        var parsed = ParseDateTime(value);
        if (parsed is null)
        {
            return null;
        }

        // Windows affiche 30.11.1999 pour une tâche jamais exécutée.
        return parsed.Value.Year == 1999 ? null : parsed;
    }

    private static bool LooksLikeMissingTask(ProcessResult process)
    {
        var text = Normalize(process.StandardOutput + " " + process.StandardError);
        return text.Contains("introuvable", StringComparison.Ordinal)
               || text.Contains("cannot find", StringComparison.Ordinal)
               || text.Contains("ne peut pas trouver", StringComparison.Ordinal)
               || text.Contains("does not exist", StringComparison.Ordinal)
               || text.Contains("n'existe pas", StringComparison.Ordinal)
               || text.Contains("no se encuentra", StringComparison.Ordinal);
    }

    private static string CleanMessage(ProcessResult process)
    {
        var text = string.IsNullOrWhiteSpace(process.StandardError)
            ? process.StandardOutput
            : process.StandardError;

        text = string.Join(
            " ",
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));

        if (text.Length > 500)
        {
            text = text[..500] + "…";
        }

        return text.Length == 0
            ? $"schtasks.exe a retourné le code {process.ExitCode}."
            : text;
    }
}
