using System.Globalization;

namespace Memo.Core;

internal sealed record ExportFileInfo(
    MediaRole Role,
    string StoredPath,
    string Sha256,
    long Size,
    string Format,
    string EntryPath,
    string? ArchivePath);

internal sealed record ExportMemoryInfo(
    long MemoryId,
    string ExternalKey,
    MemoryStatus Status,
    DateOnly? LocalDate,
    TimeOnly? LocalTime,
    string? Note,
    IReadOnlyList<ExportFileInfo> Files,
    IReadOnlyList<DateCandidateInfo> Candidates,
    DateOverrideInfo? Override);

public sealed partial class MemoCatalog
{
    internal async Task<ExportMemoryInfo?> GetExportMemoryAsync(long memoryId, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        string? externalKey = null;
        string statusText = "unknown";
        using (var memoryCommand = CreateCommand(
                   connection,
                   "SELECT external_key, status FROM memories WHERE id=@id;",
                   ("@id", memoryId)))
        {
            await using var reader = await memoryCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            externalKey = reader.GetString(0);
            statusText = reader.GetString(1);
        }

        var dateSql = DateCte +
                      " SELECT e.local_date, e.local_time FROM effective_date e WHERE e.memory_id=@id;";
        DateOnly? localDate = null;
        TimeOnly? localTime = null;
        using (var dateCommand = CreateCommand(connection, dateSql, ("@id", memoryId)))
        {
            await using var reader = await dateCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                localDate = DateText.ParseDate(reader.IsDBNull(0) ? null : reader.GetString(0));
                localTime = DateText.ParseTime(reader.IsDBNull(1) ? null : reader.GetString(1));
            }
        }

        var files = new List<ExportFileInfo>();
        var seenRoles = new HashSet<string>(StringComparer.Ordinal);
        using (var fileCommand = CreateCommand(
                   connection,
                   """
                   SELECT mf.role, a.stored_path, sf.sha256, sf.size, sf.detected_format,
                          sf.entry_path, ar.source_path
                     FROM memory_files mf
                     JOIN source_files sf ON sf.id=mf.source_file_id
                     JOIN assets a ON a.sha256=sf.sha256
                     LEFT JOIN archives ar ON ar.id=sf.archive_id
                    WHERE mf.memory_id=@id AND mf.role IN ('main', 'overlay')
                    ORDER BY CASE mf.role WHEN 'main' THEN 0 ELSE 1 END, mf.source_file_id;
                   """,
                   ("@id", memoryId)))
        {
            await using var reader = await fileCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var role = reader.GetString(0);
                if (!seenRoles.Add(role))
                {
                    continue;
                }

                files.Add(new ExportFileInfo(
                    RoleFromText(role),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        var candidates = await GetDateCandidatesAsync(memoryId, ct).ConfigureAwait(false);
        var overrideInfo = await GetDateOverrideAsync(memoryId, ct).ConfigureAwait(false);
        var noteValue = await ScalarAsync(
            connection,
            "SELECT text FROM memory_notes WHERE memory_id=@id;",
            ct,
            ("@id", memoryId)).ConfigureAwait(false);

        return new ExportMemoryInfo(
            memoryId,
            externalKey,
            StatusFromText(statusText),
            localDate,
            localTime,
            noteValue as string,
            files,
            candidates,
            overrideInfo);
    }

    internal static string FormatExportFileName(
        DateOnly? localDate,
        TimeOnly? localTime,
        string externalKey,
        string extension,
        bool overlay)
    {
        var datePart = localDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "00000000";
        var timePart = localTime?.ToString("HHmmss", CultureInfo.InvariantCulture) ?? "000000";
        var safeKey = new string(externalKey
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_')
            .ToArray());
        if (safeKey.Length == 0)
        {
            safeKey = "souvenir";
        }

        return $"{datePart}-{timePart}-{safeKey}{(overlay ? "-overlay" : string.Empty)}.{extension}";
    }
}
