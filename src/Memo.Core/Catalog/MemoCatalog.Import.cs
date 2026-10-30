using System.Globalization;

namespace Memo.Core;

internal sealed record ArchiveRegistration(long Id, bool Created);

internal sealed record ArchiveLookup(long Id, string Status);

internal sealed record SourceFileRegistration(long Id, bool Created);

internal sealed record MemoryRegistration(long Id, bool Created);

internal sealed record AssetRegistration(long Id, bool Created);

internal sealed record AssetWrite(
    string Sha256,
    string Format,
    int? Width,
    int? Height,
    int Rotation,
    long? DurationMs,
    bool HasAudio,
    string StoredPath,
    long Size,
    DateTimeOffset? CreationTimeUtc,
    DateOnly? ExifDate,
    string? ExifRaw,
    DateSource ExifSource);

public sealed partial class MemoCatalog
{
    internal async Task<long> BeginBatchAsync(long spaceId, string? note, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var exists = await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM spaces WHERE id=@id;",
            ct,
            ("@id", spaceId)).ConfigureAwait(false);
        if (exists == 0)
        {
            throw new ArgumentException($"Espace source {spaceId} introuvable.", nameof(spaceId));
        }

        using var command = CreateCommand(
            connection,
            """
            INSERT INTO batches (space_id, started_at, completed_at, status, note)
            VALUES (@space, @started, NULL, 'running', @note)
            RETURNING id;
            """,
            ("@space", spaceId),
            ("@started", DateText.NowUtc()),
            ("@note", string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    internal async Task FinishBatchAsync(long batchId, string status, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE batches SET status=@status, completed_at=@completed WHERE id=@id;",
            ct,
            ("@status", status),
            ("@completed", DateText.NowUtc()),
            ("@id", batchId)).ConfigureAwait(false);
    }

    internal async Task<ArchiveLookup?> FindArchiveAsync(
        long spaceId,
        string sha256,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            "SELECT id, status FROM archives WHERE space_id=@space AND sha256=@sha;",
            ("@space", spaceId),
            ("@sha", sha256));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ArchiveLookup(reader.GetInt64(0), reader.GetString(1));
    }

    internal async Task SetArchiveStatusAsync(
        long archiveId,
        string status,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE archives SET status=@status WHERE id=@id;",
            ct,
            ("@status", status),
            ("@id", archiveId)).ConfigureAwait(false);
    }

    internal async Task<ArchiveRegistration> RegisterArchiveAsync(
        long batchId,
        long spaceId,
        string sourcePath,
        string sha256,
        long size,
        string status,
        int entryCount,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var inserted = await ExecuteAsync(
            connection,
            """
            INSERT OR IGNORE INTO archives
                (batch_id, space_id, source_path, sha256, size, status, entry_count, created_at)
            VALUES (@batch, @space, @path, @sha, @size, @status, @entries, @created);
            """,
            ct,
            ("@batch", batchId),
            ("@space", spaceId),
            ("@path", sourcePath),
            ("@sha", sha256),
            ("@size", size),
            ("@status", status),
            ("@entries", entryCount),
            ("@created", DateText.NowUtc())).ConfigureAwait(false);

        var id = await ScalarLongAsync(
            connection,
            "SELECT id FROM archives WHERE space_id=@space AND sha256=@sha;",
            ct,
            ("@space", spaceId),
            ("@sha", sha256)).ConfigureAwait(false);
        return new ArchiveRegistration(id, inserted > 0);
    }

    internal async Task<SourceFileRegistration> AddSourceFileAsync(
        long spaceId,
        long archiveId,
        string entryPath,
        string sha256,
        long size,
        string declaredExtension,
        string detectedFormat,
        string role,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var inserted = await ExecuteAsync(
            connection,
            """
            INSERT OR IGNORE INTO source_files
                (space_id, archive_id, entry_path, sha256, size, declared_ext, detected_format, role, created_at)
            VALUES (@space, @archive, @entry, @sha, @size, @ext, @format, @role, @created);
            """,
            ct,
            ("@space", spaceId),
            ("@archive", archiveId),
            ("@entry", entryPath),
            ("@sha", sha256),
            ("@size", size),
            ("@ext", declaredExtension),
            ("@format", detectedFormat),
            ("@role", role),
            ("@created", DateText.NowUtc())).ConfigureAwait(false);

        var id = await ScalarLongAsync(
            connection,
            """
            SELECT id FROM source_files
             WHERE space_id=@space AND archive_id=@archive AND entry_path=@entry;
            """,
            ct,
            ("@space", spaceId),
            ("@archive", archiveId),
            ("@entry", entryPath)).ConfigureAwait(false);
        return new SourceFileRegistration(id, inserted > 0);
    }

    internal async Task<AssetRegistration> EnsureAssetAsync(AssetWrite asset, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var inserted = await ExecuteAsync(
            connection,
            """
            INSERT OR IGNORE INTO assets
                (sha256, format, width, height, rotation, duration_ms, has_audio, stored_path, size,
                 creation_time_utc, exif_date, exif_raw, exif_source)
            VALUES (@sha, @format, @width, @height, @rotation, @duration, @audio, @path, @size,
                    @creation, @exifdate, @exifraw, @exifsource);
            """,
            ct,
            ("@sha", asset.Sha256),
            ("@format", asset.Format),
            ("@width", asset.Width),
            ("@height", asset.Height),
            ("@rotation", asset.Rotation),
            ("@duration", asset.DurationMs),
            ("@audio", asset.HasAudio ? 1 : 0),
            ("@path", asset.StoredPath),
            ("@size", asset.Size),
            ("@creation", asset.CreationTimeUtc is null ? null : DateText.Instant(asset.CreationTimeUtc.Value)),
            ("@exifdate", DateText.Date(asset.ExifDate)),
            ("@exifraw", asset.ExifRaw),
            ("@exifsource", (int)asset.ExifSource)).ConfigureAwait(false);

        var id = await ScalarLongAsync(
            connection,
            "SELECT id FROM assets WHERE sha256=@sha;",
            ct,
            ("@sha", asset.Sha256)).ConfigureAwait(false);
        return new AssetRegistration(id, inserted > 0);
    }

    internal async Task<MemoryRegistration> EnsureMemoryAsync(
        long spaceId,
        string externalKey,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var now = DateText.NowUtc();
        var inserted = await ExecuteAsync(
            connection,
            """
            INSERT OR IGNORE INTO memories (space_id, external_key, status, created_at, updated_at)
            VALUES (@space, @key, 'unknown', @now, @now);
            """,
            ct,
            ("@space", spaceId),
            ("@key", externalKey),
            ("@now", now)).ConfigureAwait(false);

        var id = await ScalarLongAsync(
            connection,
            "SELECT id FROM memories WHERE space_id=@space AND external_key=@key;",
            ct,
            ("@space", spaceId),
            ("@key", externalKey)).ConfigureAwait(false);
        return new MemoryRegistration(id, inserted > 0);
    }

    internal async Task LinkMemoryAsync(
        long memoryId,
        long assetId,
        long sourceFileId,
        MediaRole role,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var roleText = RoleToText(role);
        await ExecuteAsync(
            connection,
            """
            INSERT OR IGNORE INTO memory_assets (memory_id, asset_id, role)
            VALUES (@memory, @asset, @role);
            """,
            ct,
            ("@memory", memoryId),
            ("@asset", assetId),
            ("@role", roleText)).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            INSERT OR IGNORE INTO memory_files (memory_id, source_file_id, role)
            VALUES (@memory, @file, @role);
            """,
            ct,
            ("@memory", memoryId),
            ("@file", sourceFileId),
            ("@role", roleText)).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE memories SET updated_at=@now WHERE id=@id;",
            ct,
            ("@now", DateText.NowUtc()),
            ("@id", memoryId)).ConfigureAwait(false);
    }

    internal async Task EnsureDateCandidateAsync(
        long memoryId,
        string raw,
        DateSource source,
        DateMeaning meaning,
        DatePrecision precision,
        string? timeZoneId,
        string? instantUtc,
        DateOnly? localDate,
        TimeOnly? localTime,
        double confidence,
        CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var existing = await ScalarAsync(
            connection,
            """
            SELECT id FROM date_candidates
             WHERE memory_id=@memory AND source=@source AND raw=@raw AND meaning=@meaning
             LIMIT 1;
            """,
            ct,
            ("@memory", memoryId),
            ("@source", (int)source),
            ("@raw", raw),
            ("@meaning", (int)meaning)).ConfigureAwait(false);

        if (existing is not null)
        {
            await ExecuteAsync(
                connection,
                """
                UPDATE date_candidates
                   SET precision=@precision, timezone=@tz, instant_utc=@instant,
                       local_date=@date, local_time=@time, confidence=@confidence
                 WHERE id=@id;
                """,
                ct,
                ("@precision", (int)precision),
                ("@tz", timeZoneId),
                ("@instant", instantUtc),
                ("@date", DateText.Date(localDate)),
                ("@time", DateText.Time(localTime)),
                ("@confidence", confidence),
                ("@id", Convert.ToInt64(existing, CultureInfo.InvariantCulture))).ConfigureAwait(false);
            return;
        }

        await ExecuteAsync(
            connection,
            """
            INSERT INTO date_candidates
                (memory_id, raw, source, meaning, precision, timezone, instant_utc,
                 local_date, local_time, confidence)
            VALUES (@memory, @raw, @source, @meaning, @precision, @tz, @instant,
                    @date, @time, @confidence);
            """,
            ct,
            ("@memory", memoryId),
            ("@raw", raw),
            ("@source", (int)source),
            ("@meaning", (int)meaning),
            ("@precision", (int)precision),
            ("@tz", timeZoneId),
            ("@instant", instantUtc),
            ("@date", DateText.Date(localDate)),
            ("@time", DateText.Time(localTime)),
            ("@confidence", confidence)).ConfigureAwait(false);
    }

    internal async Task<MemoryStatus> UpdateMemoryStatusAsync(long memoryId, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var mainCount = await ScalarLongAsync(
            connection,
            """
            SELECT COUNT(DISTINCT a.sha256)
              FROM memory_assets ma
              JOIN assets a ON a.id=ma.asset_id
             WHERE ma.memory_id=@id AND ma.role='main';
            """,
            ct,
            ("@id", memoryId)).ConfigureAwait(false);
        var overlayCount = await ScalarLongAsync(
            connection,
            """
            SELECT COUNT(DISTINCT a.sha256)
              FROM memory_assets ma
              JOIN assets a ON a.id=ma.asset_id
             WHERE ma.memory_id=@id AND ma.role='overlay';
            """,
            ct,
            ("@id", memoryId)).ConfigureAwait(false);

        var status = mainCount switch
        {
            > 1 => MemoryStatus.Conflict,
            > 0 when overlayCount > 0 => MemoryStatus.Complete,
            > 0 => MemoryStatus.MediaOnly,
            _ when overlayCount > 0 => MemoryStatus.OverlayOnly,
            _ => MemoryStatus.Unknown,
        };

        await ExecuteAsync(
            connection,
            "UPDATE memories SET status=@status, updated_at=@now WHERE id=@id;",
            ct,
            ("@status", StatusToText(status)),
            ("@now", DateText.NowUtc()),
            ("@id", memoryId)).ConfigureAwait(false);
        return status;
    }

    public async Task SetNoteAsync(long memoryId, string? note, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(note))
        {
            await ExecuteAsync(connection, "DELETE FROM memory_notes WHERE memory_id=@id;", ct, ("@id", memoryId))
                .ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO memory_notes (memory_id, text, updated_at)
                VALUES (@id, @text, @now)
                ON CONFLICT(memory_id) DO UPDATE SET text=excluded.text, updated_at=excluded.updated_at;
                """,
                ct,
                ("@id", memoryId),
                ("@text", note.Trim()),
                ("@now", DateText.NowUtc())).ConfigureAwait(false);
        }

        await TouchMemoryAsync(connection, memoryId, ct).ConfigureAwait(false);
    }

    internal async Task RecomputeAllStatusesAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var ids = new List<long>();
        using (var command = CreateCommand(connection, "SELECT id FROM memories;"))
        {
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in ids)
        {
            await UpdateMemoryStatusAsync(id, ct).ConfigureAwait(false);
        }
    }
}
