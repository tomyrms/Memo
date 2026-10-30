using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Memo.Core;

public sealed record FaceInfo(
    long Id,
    long MemoryId,
    long? GroupId,
    int X,
    int Y,
    int Width,
    int Height,
    DateTimeOffset CreatedAt);

public sealed class AnalysisStore : IAnalysisStore
{
    private readonly MemoCatalog _catalog;

    internal AnalysisStore(MemoCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task SetOcrTextAsync(long memoryId, string text, string engine, CancellationToken ct = default)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        await EnsureMemoryExistsAsync(memoryId, ct).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await MemoCatalog.ExecuteAsync(
            connection,
            """
            INSERT INTO analysis_ocr (memory_id, text, engine, updated_at)
            VALUES (@id, @text, @engine, @now)
            ON CONFLICT(memory_id) DO UPDATE SET
                text=excluded.text, engine=excluded.engine, updated_at=excluded.updated_at;
            """,
            ct,
            ("@id", memoryId),
            ("@text", text),
            ("@engine", string.IsNullOrWhiteSpace(engine) ? "unknown" : engine.Trim()),
            ("@now", DateText.NowUtc())).ConfigureAwait(false);
    }

    public async Task<string?> GetOcrTextAsync(long memoryId, CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        var value = await MemoCatalog.ScalarAsync(
            connection,
            "SELECT text FROM analysis_ocr WHERE memory_id=@id;",
            ct,
            ("@id", memoryId)).ConfigureAwait(false);
        return value as string;
    }

    public async Task ClearOcrAsync(CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await MemoCatalog.ExecuteAsync(connection, "DELETE FROM analysis_ocr;", ct).ConfigureAwait(false);
    }

    public async Task SetVisualHashAsync(long memoryId, ulong hash, CancellationToken ct = default)
    {
        await EnsureMemoryExistsAsync(memoryId, ct).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await MemoCatalog.ExecuteAsync(
            connection,
            """
            INSERT INTO analysis_visual (memory_id, hash, updated_at)
            VALUES (@id, @hash, @now)
            ON CONFLICT(memory_id) DO UPDATE SET hash=excluded.hash, updated_at=excluded.updated_at;
            """,
            ct,
            ("@id", memoryId),
            ("@hash", unchecked((long)hash)),
            ("@now", DateText.NowUtc())).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(long MemoryId, ulong Hash)>> GetVisualHashesAsync(
        CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = MemoCatalog.CreateCommand(
            connection,
            "SELECT memory_id, hash FROM analysis_visual ORDER BY memory_id;");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<(long, ulong)>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add((reader.GetInt64(0), unchecked((ulong)reader.GetInt64(1))));
        }

        return result;
    }

    public async Task ClearVisualAsync(CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await MemoCatalog.ExecuteAsync(connection, "DELETE FROM analysis_visual;", ct).ConfigureAwait(false);
    }

    public async Task<long> CreatePersonGroupAsync(string? name, CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = MemoCatalog.CreateCommand(
            connection,
            "INSERT INTO person_groups (name, created_at) VALUES (@name, @now) RETURNING id;",
            ("@name", string.IsNullOrWhiteSpace(name) ? null : name.Trim()),
            ("@now", DateText.NowUtc()));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<PersonGroupInfo>> GetPersonGroupsAsync(CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = MemoCatalog.CreateCommand(
            connection,
            """
            SELECT pg.id,
                   pg.name,
                   (SELECT COUNT(*) FROM faces f WHERE f.group_id=pg.id) AS face_count,
                   (SELECT COUNT(DISTINCT f.memory_id) FROM faces f WHERE f.group_id=pg.id) AS memory_count,
                   (SELECT COALESCE(t.relative_path, a.stored_path)
                      FROM faces f2
                      LEFT JOIN thumbnails t ON t.memory_id=f2.memory_id
                      LEFT JOIN memory_files mf
                             ON mf.source_file_id = (
                                 SELECT MIN(mf2.source_file_id)
                                   FROM memory_files mf2
                                  WHERE mf2.memory_id=f2.memory_id AND mf2.role='main')
                      LEFT JOIN source_files sf ON sf.id=mf.source_file_id
                      LEFT JOIN assets a ON a.sha256=sf.sha256
                     WHERE f2.group_id=pg.id
                     ORDER BY f2.id
                     LIMIT 1) AS cover_path
              FROM person_groups pg
             ORDER BY pg.name IS NULL, pg.name COLLATE NOCASE, pg.id;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<PersonGroupInfo>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string? cover = null;
            if (!reader.IsDBNull(4))
            {
                var relative = reader.GetString(4);
                var path = ImportPaths.ToPlatformPath(_catalog.RootPath, relative);
                if (File.Exists(path))
                {
                    cover = path;
                }
            }

            result.Add(new PersonGroupInfo(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(3), CultureInfo.InvariantCulture),
                cover));
        }

        return result;
    }

    public async Task RenamePersonGroupAsync(long groupId, string name, CancellationToken ct = default)
    {
        var cleanName = name?.Trim();
        if (string.IsNullOrEmpty(cleanName))
        {
            throw new ArgumentException("Le nom du groupe est obligatoire.", nameof(name));
        }

        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        var affected = await MemoCatalog.ExecuteAsync(
            connection,
            "UPDATE person_groups SET name=@name WHERE id=@id;",
            ct,
            ("@name", cleanName),
            ("@id", groupId)).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Groupe de personnes {groupId} introuvable.");
        }
    }

    public async Task MergePersonGroupsAsync(
        long targetGroupId,
        long sourceGroupId,
        CancellationToken ct = default)
    {
        if (targetGroupId == sourceGroupId)
        {
            throw new ArgumentException("Les deux groupes à fusionner doivent être différents.");
        }

        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        var targetExists = await MemoCatalog.ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM person_groups WHERE id=@id;",
            ct,
            ("@id", targetGroupId)).ConfigureAwait(false);
        var sourceExists = await MemoCatalog.ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM person_groups WHERE id=@id;",
            ct,
            ("@id", sourceGroupId)).ConfigureAwait(false);
        if (targetExists == 0 || sourceExists == 0)
        {
            throw new KeyNotFoundException("Groupe de personnes introuvable.");
        }

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        using (var update = MemoCatalog.CreateCommand(
                   connection,
                   "UPDATE faces SET group_id=@target WHERE group_id=@source;",
                   ("@target", targetGroupId),
                   ("@source", sourceGroupId)))
        {
            update.Transaction = (SqliteTransaction)transaction;
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var delete = MemoCatalog.CreateCommand(
                   connection,
                   "DELETE FROM person_groups WHERE id=@source;",
                   ("@source", sourceGroupId)))
        {
            delete.Transaction = (SqliteTransaction)transaction;
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeletePersonGroupAsync(long groupId, CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        var affected = await MemoCatalog.ExecuteAsync(
            connection,
            "DELETE FROM person_groups WHERE id=@id;",
            ct,
            ("@id", groupId)).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Groupe de personnes {groupId} introuvable.");
        }
    }

    public async Task AssignFaceAsync(long faceId, long? groupId, CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        if (groupId is not null)
        {
            var exists = await MemoCatalog.ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM person_groups WHERE id=@id;",
                ct,
                ("@id", groupId.Value)).ConfigureAwait(false);
            if (exists == 0)
            {
                throw new KeyNotFoundException($"Groupe de personnes {groupId} introuvable.");
            }
        }

        var affected = await MemoCatalog.ExecuteAsync(
            connection,
            "UPDATE faces SET group_id=@group WHERE id=@id;",
            ct,
            ("@group", groupId),
            ("@id", faceId)).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Visage {faceId} introuvable.");
        }
    }

    public async Task ClearFacesAsync(CancellationToken ct = default)
    {
        // Les noms de groupes viennent de l'utilisateur : effacer les détections ne les supprime pas.
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await MemoCatalog.ExecuteAsync(connection, "DELETE FROM faces;", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<long>> GetMemoriesForPersonAsync(
        long groupId,
        CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = MemoCatalog.CreateCommand(
            connection,
            "SELECT DISTINCT memory_id FROM faces WHERE group_id=@id ORDER BY memory_id;",
            ("@id", groupId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<long>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    public async Task<long> AddFaceAsync(
        long memoryId,
        int x,
        int y,
        int width,
        int height,
        long? groupId = null,
        byte[]? descriptor = null,
        CancellationToken ct = default)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Les dimensions du visage doivent être positives.");
        }

        await EnsureMemoryExistsAsync(memoryId, ct).ConfigureAwait(false);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        if (groupId is not null)
        {
            var exists = await MemoCatalog.ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM person_groups WHERE id=@id;",
                ct,
                ("@id", groupId.Value)).ConfigureAwait(false);
            if (exists == 0)
            {
                throw new KeyNotFoundException($"Groupe de personnes {groupId} introuvable.");
            }
        }

        using var command = MemoCatalog.CreateCommand(
            connection,
            """
            INSERT INTO faces (memory_id, group_id, x, y, w, h, descriptor, created_at)
            VALUES (@memory, @group, @x, @y, @w, @h, @descriptor, @now)
            RETURNING id;
            """,
            ("@memory", memoryId),
            ("@group", groupId),
            ("@x", x),
            ("@y", y),
            ("@w", width),
            ("@h", height),
            ("@descriptor", descriptor),
            ("@now", DateText.NowUtc()));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<FaceInfo>> GetFacesAsync(
        long? memoryId = null,
        long? groupId = null,
        CancellationToken ct = default)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        var sql = "SELECT id, memory_id, group_id, x, y, w, h, created_at FROM faces";
        var filters = new List<string>();
        var parameters = new List<(string, object?)>();
        if (memoryId is not null)
        {
            filters.Add("memory_id=@memory");
            parameters.Add(("@memory", memoryId.Value));
        }

        if (groupId is not null)
        {
            filters.Add("group_id=@group");
            parameters.Add(("@group", groupId.Value));
        }

        if (filters.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", filters);
        }

        sql += " ORDER BY id;";
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<FaceInfo>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new FaceInfo(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                Convert.ToInt32(reader.GetInt64(3), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(4), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(5), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(6), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    private async Task EnsureMemoryExistsAsync(long memoryId, CancellationToken ct)
    {
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        var exists = await MemoCatalog.ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM memories WHERE id=@id;",
            ct,
            ("@id", memoryId)).ConfigureAwait(false);
        if (exists == 0)
        {
            throw new KeyNotFoundException($"Souvenir {memoryId} introuvable.");
        }
    }
}
