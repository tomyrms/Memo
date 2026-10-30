using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Memo.Core;

public sealed partial class MemoCatalog : IMemoCatalog
{
    internal const string RoleMain = "main";
    internal const string RoleOverlay = "overlay";
    internal const string RoleChat = "chat";

    private readonly string _connectionString;
    private LibraryInfo _info;

    internal MemoCatalog(LibraryInfo info)
    {
        _info = info;
        DatabasePath = Path.Combine(info.RootPath, "catalog.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
    }

    public LibraryInfo Info => _info;
    public string RootPath => _info.RootPath;
    public string TimeZoneId => _info.TimeZoneId;
    internal string DatabasePath { get; }

    public event EventHandler<string>? TimeZoneChanged;

    internal static async Task<MemoCatalog> OpenAsync(LibraryInfo info, CancellationToken ct = default)
    {
        var catalog = new MemoCatalog(info);
        await catalog.InitializeAsync(ct).ConfigureAwait(false);
        return catalog;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        await using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            await journal.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        long version;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt64(await read.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        if (version > CatalogSchema.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Le catalogue « {DatabasePath} » utilise une version plus récente ({version}) que cette version de Memo.");
        }

        if (version == 0)
        {
            await using var create = connection.CreateCommand();
            create.CommandText = CatalogSchema.Create;
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var setVersion = connection.CreateCommand();
            setVersion.CommandText = $"PRAGMA user_version = {CatalogSchema.CurrentVersion};";
            await setVersion.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var pragmas = connection.CreateCommand();
            pragmas.CommandText =
                "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000; PRAGMA synchronous=NORMAL;";
            await pragmas.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static SqliteCommand CreateCommand(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    internal static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    internal static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        var value = await ScalarAsync(connection, sql, ct, parameters).ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    internal static string StatusToText(MemoryStatus status) => status switch
    {
        MemoryStatus.Complete => "complete",
        MemoryStatus.MediaOnly => "mediaonly",
        MemoryStatus.OverlayOnly => "overlayonly",
        MemoryStatus.Conflict => "conflict",
        _ => "unknown",
    };

    internal static MemoryStatus StatusFromText(string? status) => status switch
    {
        "complete" => MemoryStatus.Complete,
        "mediaonly" => MemoryStatus.MediaOnly,
        "overlayonly" => MemoryStatus.OverlayOnly,
        "conflict" => MemoryStatus.Conflict,
        _ => MemoryStatus.Unknown,
    };

    internal static string RoleToText(MediaRole role) => role == MediaRole.Overlay ? RoleOverlay : RoleMain;

    internal static MediaRole RoleFromText(string? role) =>
        string.Equals(role, RoleOverlay, StringComparison.Ordinal) ? MediaRole.Overlay : MediaRole.Main;

    internal static string CanonicalExtension(string format) => format switch
    {
        "jpeg" => "jpg",
        "png" => "png",
        "webp" => "webp",
        "mp4" => "mp4",
        "mov" => "mov",
        "heic" => "heic",
        "avif" => "avif",
        _ => "bin",
    };

    internal static bool IsVideoFormat(string? format) =>
        string.Equals(format, "mp4", StringComparison.Ordinal) ||
        string.Equals(format, "mov", StringComparison.Ordinal);

    internal static MediaKind KindForFormat(string? format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return MediaKind.Unknown;
        }

        return IsVideoFormat(format) ? MediaKind.Video : MediaKind.Photo;
    }

    public async Task<IReadOnlyList<SpaceInfo>> GetSpacesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            "SELECT id, name, account_hint, created_at FROM spaces ORDER BY id;");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<SpaceInfo>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new SpaceInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    public async Task<SpaceInfo> CreateSpaceAsync(
        string name,
        string? accountHint = null,
        CancellationToken ct = default)
    {
        var cleanName = name?.Trim();
        if (string.IsNullOrEmpty(cleanName))
        {
            throw new ArgumentException("Le nom de l'espace est obligatoire.", nameof(name));
        }

        var hint = string.IsNullOrWhiteSpace(accountHint) ? null : accountHint.Trim();
        var createdAt = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            """
            INSERT INTO spaces (name, account_hint, created_at)
            VALUES (@name, @hint, @created)
            RETURNING id;
            """,
            ("@name", cleanName),
            ("@hint", hint),
            ("@created", DateText.Instant(createdAt)));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return new SpaceInfo(id, cleanName, hint, createdAt);
    }

    public async Task RenameSpaceAsync(long spaceId, string name, CancellationToken ct = default)
    {
        var cleanName = name?.Trim();
        if (string.IsNullOrEmpty(cleanName))
        {
            throw new ArgumentException("Le nom de l'espace est obligatoire.", nameof(name));
        }

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            "UPDATE spaces SET name=@name WHERE id=@id;",
            ct,
            ("@name", cleanName),
            ("@id", spaceId)).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Espace source {spaceId} introuvable.");
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;";
        pragmas.ExecuteNonQuery();
        return connection;
    }

    public string GetAssetPath(long assetId)
    {
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, "SELECT stored_path FROM assets WHERE id=@id;", ("@id", assetId));
        var value = command.ExecuteScalar();
        if (value is not string relative)
        {
            throw new KeyNotFoundException($"Média {assetId} introuvable.");
        }

        return ImportPaths.ToPlatformPath(RootPath, relative);
    }

    public string? GetThumbnailPath(long memoryId)
    {
        using var connection = OpenConnection();
        using var command = CreateCommand(
            connection,
            "SELECT relative_path FROM thumbnails WHERE memory_id=@id;",
            ("@id", memoryId));
        var value = command.ExecuteScalar();
        if (value is not string relative)
        {
            return null;
        }

        var path = ImportPaths.ToPlatformPath(RootPath, relative);
        return File.Exists(path) ? path : null;
    }

    public string? GetCompositePath(long memoryId)
    {
        foreach (var extension in new[] { "png", "jpg", "webp" })
        {
            var path = Path.Combine(RootPath, "derived", "composites", $"{memoryId}.{extension}");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public async Task SetThumbnailAsync(
        long memoryId,
        string? relativePath,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            await ExecuteAsync(connection, "DELETE FROM thumbnails WHERE memory_id=@id;", ct, ("@id", memoryId))
                .ConfigureAwait(false);
            await TouchMemoryAsync(connection, memoryId, ct).ConfigureAwait(false);
            return;
        }

        if (!ImportPaths.TryNormalize(relativePath, out var normalized, out var error))
        {
            throw new ArgumentException($"Chemin de miniature invalide : {error}.", nameof(relativePath));
        }

        var full = ImportPaths.ToPlatformPath(RootPath, normalized);
        if (!ImportPaths.IsInsideRoot(RootPath, full))
        {
            throw new ArgumentException("La miniature doit rester dans la bibliothèque.", nameof(relativePath));
        }

        await ExecuteAsync(
            connection,
            """
            INSERT INTO thumbnails (memory_id, relative_path, updated_at)
            VALUES (@id, @path, @now)
            ON CONFLICT(memory_id) DO UPDATE SET relative_path=excluded.relative_path, updated_at=excluded.updated_at;
            """,
            ct,
            ("@id", memoryId),
            ("@path", normalized),
            ("@now", DateText.NowUtc())).ConfigureAwait(false);
        await TouchMemoryAsync(connection, memoryId, ct).ConfigureAwait(false);
    }

    public async Task RecalculateClassificationAsync(string timeZoneId, CancellationToken ct = default)
    {
        var timeZone = TimeZoneHelper.Resolve(timeZoneId);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var candidates = new List<(long Id, string Instant)>();
        using (var read = CreateCommand(
                   connection,
                   "SELECT id, instant_utc FROM date_candidates WHERE instant_utc IS NOT NULL;"))
        {
            read.Transaction = (SqliteTransaction)transaction;
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(1))
                {
                    candidates.Add((reader.GetInt64(0), reader.GetString(1)));
                }
            }
        }

        foreach (var (id, rawInstant) in candidates)
        {
            var instant = DateText.ParseInstant(rawInstant);
            if (instant is null)
            {
                continue;
            }

            var (date, time) = DateText.Local(instant.Value, timeZone);
            using var update = CreateCommand(
                connection,
                """
                UPDATE date_candidates
                SET local_date=@date, local_time=@time, timezone=@tz
                WHERE id=@id;
                """,
                ("@date", DateText.Date(date)),
                ("@time", DateText.Time(time)),
                ("@tz", timeZone.Id),
                ("@id", id));
            update.Transaction = (SqliteTransaction)transaction;
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await UpdateSettingsTimeZoneAsync(connection, (SqliteTransaction)transaction, timeZone.Id, ct)
            .ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        _info = _info with { TimeZoneId = timeZone.Id };
        if (File.Exists(Path.Combine(RootPath, LibraryManifest.FileName)))
        {
            await LibraryManifest.UpdateTimeZoneAsync(RootPath, timeZone.Id, ct).ConfigureAwait(false);
        }

        TimeZoneChanged?.Invoke(this, timeZone.Id);
    }

    private static async Task UpdateSettingsTimeZoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string timeZoneId,
        CancellationToken ct)
    {
        using var read = CreateCommand(connection, "SELECT value FROM settings WHERE key='app';");
        read.Transaction = transaction;
        var raw = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is not string json)
        {
            return;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        if (node is not JsonObject settings)
        {
            return;
        }

        settings["timeZoneId"] = timeZoneId;
        using var update = CreateCommand(
            connection,
            "UPDATE settings SET value=@value WHERE key='app';",
            ("@value", settings.ToJsonString(JsonSupport.Compact)));
        update.Transaction = transaction;
        await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task TouchMemoryAsync(SqliteConnection connection, long memoryId, CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            "UPDATE memories SET updated_at=@now WHERE id=@id;",
            ct,
            ("@now", DateText.NowUtc()),
            ("@id", memoryId)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImportBatchInfo>> GetBatchesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            """
            SELECT b.id, b.space_id, s.name, b.started_at, b.completed_at, b.status, b.note,
                   (SELECT COUNT(*) FROM archives a WHERE a.batch_id=b.id) AS archives,
                   (SELECT COUNT(DISTINCT mf.memory_id)
                      FROM archives a
                      JOIN source_files sf ON sf.archive_id=a.id
                      JOIN memory_files mf ON mf.source_file_id=sf.id
                     WHERE a.batch_id=b.id) AS memories
              FROM batches b
              JOIN spaces s ON s.id=b.space_id
             ORDER BY b.id DESC;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<ImportBatchInfo>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ImportBatchInfo(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(4)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(5),
                Convert.ToInt32(reader.GetInt64(7), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(8), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return result;
    }

    public async Task<StorageStats> GetStorageStatsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var memories = Convert.ToInt32(
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM memories;", ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        var assets = Convert.ToInt32(
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM assets;", ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        var sources = Convert.ToInt32(
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM source_files;", ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        var spaces = Convert.ToInt32(
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM spaces;", ct).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        var sourceBytes = await ScalarLongAsync(connection, "SELECT COALESCE(SUM(size),0) FROM assets;", ct)
            .ConfigureAwait(false);

        long derivedBytes = 0;
        var derivedRoot = Path.Combine(RootPath, "derived");
        if (Directory.Exists(derivedRoot))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(derivedRoot, "*", SearchOption.AllDirectories))
                {
                    derivedBytes += new FileInfo(file).Length;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new StorageStats(memories, assets, sources, sourceBytes, derivedBytes, spaces);
    }

    public async Task VacuumAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal async Task VacuumIntoAsync(string destinationPath, CancellationToken ct)
    {
        var full = Path.GetFullPath(destinationPath);
        if (string.Equals(full, DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La copie du catalogue ne peut pas remplacer le catalogue actif.");
        }

        if (File.Exists(full))
        {
            File.Delete(full);
        }

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"VACUUM INTO '{full.Replace("'", "''", StringComparison.Ordinal)}';";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal async Task<(int Count, long Bytes)> GetSourceStatsAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var count = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM source_files;", ct).ConfigureAwait(false);
        var bytes = await ScalarLongAsync(connection, "SELECT COALESCE(SUM(size),0) FROM assets;", ct)
            .ConfigureAwait(false);
        return (Convert.ToInt32(count, CultureInfo.InvariantCulture), bytes);
    }

    internal sealed record StoredAssetFile(string RelativePath, long Size);

    internal async Task<IReadOnlyList<StoredAssetFile>> GetStoredAssetFilesAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            "SELECT stored_path, size FROM assets ORDER BY stored_path;");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<StoredAssetFile>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new StoredAssetFile(reader.GetString(0), reader.GetInt64(1)));
        }

        return result;
    }

    public async Task<MediaInfo?> GetMediaInfoAsync(
        long memoryId,
        MediaRole role,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            """
            SELECT a.format, a.width, a.height, a.rotation, a.duration_ms, a.has_audio,
                   a.creation_time_utc, a.exif_date, a.exif_raw
              FROM memory_files mf
              JOIN source_files sf ON sf.id=mf.source_file_id
              JOIN assets a ON a.sha256=sf.sha256
             WHERE mf.memory_id=@id AND mf.role=@role
             ORDER BY mf.source_file_id
             LIMIT 1;
            """,
            ("@id", memoryId),
            ("@role", RoleToText(role)));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new MediaInfo(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetInt64(3), CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetInt64(5) != 0,
            reader.IsDBNull(6) ? null : DateText.ParseInstant(reader.GetString(6)),
            DateText.ParseDate(reader.IsDBNull(7) ? null : reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }
}
