using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Memo.Core;

public sealed partial class MemoCatalog
{
    private const string DateCte = """
        WITH ranked_dates AS (
            SELECT dc.id, dc.memory_id, dc.local_date, dc.local_time, dc.source,
                   dc.precision, dc.meaning, dc.confidence,
                   ROW_NUMBER() OVER (
                       PARTITION BY dc.memory_id
                       ORDER BY CASE dc.source
                                  WHEN 2 THEN 0
                                  WHEN 3 THEN 1
                                  WHEN 1 THEN 2
                                  WHEN 4 THEN 3
                                  ELSE 9
                                END,
                                dc.confidence DESC,
                                dc.id ASC) AS rn
              FROM date_candidates dc
             WHERE dc.local_date IS NOT NULL
        ),
        best_date AS (
            SELECT * FROM ranked_dates WHERE rn = 1
        ),
        effective_date AS (
            SELECT m.id AS memory_id,
                   COALESCE(o.local_date, b.local_date) AS local_date,
                   CASE WHEN o.memory_id IS NOT NULL THEN 5 ELSE COALESCE(b.source, 0) END AS date_source,
                   CASE WHEN o.memory_id IS NOT NULL THEN o.precision ELSE COALESCE(b.precision, 0) END AS date_precision,
                   CASE WHEN o.memory_id IS NOT NULL THEN 4 ELSE COALESCE(b.meaning, 0) END AS date_meaning,
                   CASE WHEN o.memory_id IS NOT NULL THEN 1.0 ELSE COALESCE(b.confidence, 0.0) END AS date_confidence,
                   CASE WHEN o.memory_id IS NOT NULL THEN '00:00:00' ELSE COALESCE(b.local_time, '00:00:00') END AS local_time
              FROM memories m
              LEFT JOIN best_date b ON b.memory_id = m.id
              LEFT JOIN date_overrides o ON o.memory_id = m.id
        )
        """;

    private const string CardCtes = """
        ,
        main_ranked AS (
            SELECT mf.memory_id, a.id AS asset_id, mf.source_file_id,
                   ROW_NUMBER() OVER (PARTITION BY mf.memory_id ORDER BY a.sha256 ASC, mf.source_file_id ASC) AS rn
              FROM memory_files mf
              JOIN source_files sf ON sf.id = mf.source_file_id
              JOIN assets a ON a.sha256 = sf.sha256
             WHERE mf.role = 'main'
        ),
        overlay_ranked AS (
            SELECT mf.memory_id, a.id AS asset_id, mf.source_file_id,
                   ROW_NUMBER() OVER (PARTITION BY mf.memory_id ORDER BY a.sha256 ASC, mf.source_file_id ASC) AS rn
              FROM memory_files mf
              JOIN source_files sf ON sf.id = mf.source_file_id
              JOIN assets a ON a.sha256 = sf.sha256
             WHERE mf.role = 'overlay'
        ),
        principal_main AS (
            SELECT memory_id, asset_id FROM main_ranked WHERE rn = 1
        ),
        principal_overlay AS (
            SELECT memory_id, asset_id FROM overlay_ranked WHERE rn = 1
        ),
        overlay_summary AS (
            SELECT mf.memory_id, COUNT(DISTINCT a.sha256) AS variant_count
              FROM memory_files mf
              JOIN source_files sf ON sf.id = mf.source_file_id
              JOIN assets a ON a.sha256 = sf.sha256
             WHERE mf.role = 'overlay'
             GROUP BY mf.memory_id
        )
        """;

    private const string CardSelect = """
        SELECT m.id AS memory_id,
               m.space_id,
               sp.name AS space_name,
               m.external_key,
               m.status,
               e.local_date,
               e.date_source,
               e.date_precision,
               e.date_meaning,
               pm.asset_id AS main_asset_id,
               po.asset_id AS overlay_asset_id,
               COALESCE(a.duration_ms, oa.duration_ms) AS duration_ms,
               COALESCE(a.width, oa.width) AS width,
               COALESCE(a.height, oa.height) AS height,
               COALESCE(a.format, oa.format) AS format,
               COALESCE(a.size, oa.size, 0) AS size_bytes,
               COALESCE(os.variant_count, 0) AS overlay_count,
               EXISTS (SELECT 1 FROM favorites f WHERE f.memory_id = m.id) AS is_favorite,
               EXISTS (SELECT 1 FROM exclusions x WHERE x.memory_id = m.id) AS is_excluded,
               l.lat,
               l.lon,
               l.label AS location_label,
               t.relative_path AS thumbnail_path
          FROM memories m
          JOIN spaces sp ON sp.id = m.space_id
          JOIN effective_date e ON e.memory_id = m.id
          LEFT JOIN principal_main pm ON pm.memory_id = m.id
          LEFT JOIN assets a ON a.id = pm.asset_id
          LEFT JOIN principal_overlay po ON po.memory_id = m.id
          LEFT JOIN assets oa ON oa.id = po.asset_id
          LEFT JOIN overlay_summary os ON os.memory_id = m.id
          LEFT JOIN locations l ON l.memory_id = m.id
          LEFT JOIN thumbnails t ON t.memory_id = m.id
        """;

    private sealed class CardSql
    {
        public required string Sql { get; init; }
        public required List<(string Name, object? Value)> Parameters { get; init; }
    }

    private static CardSql BuildCardSql(
        MemoryQuery query,
        long? memoryId,
        long? orderCollectionId,
        string orderClause,
        bool includePagination,
        IReadOnlyList<string>? extraWhere = null,
        IReadOnlyList<(string Name, object? Value)>? extraParameters = null)
    {
        var parameters = new List<(string, object?)>();
        var where = new List<string>();

        void Add(string name, object? value)
        {
            parameters.Add((name, value));
        }

        if (memoryId is not null)
        {
            where.Add("m.id = @memory_id");
            Add("@memory_id", memoryId.Value);
        }

        if (query.SpaceId is not null)
        {
            where.Add("m.space_id = @space_id");
            Add("@space_id", query.SpaceId.Value);
        }

        if (query.Year is not null)
        {
            where.Add("e.local_date IS NOT NULL AND CAST(substr(e.local_date, 1, 4) AS INTEGER) = @year");
            Add("@year", query.Year.Value);
        }

        if (query.Month is not null)
        {
            where.Add("e.local_date IS NOT NULL AND CAST(substr(e.local_date, 6, 2) AS INTEGER) = @month");
            Add("@month", query.Month.Value);
        }

        if (query.Day is not null)
        {
            where.Add("e.local_date IS NOT NULL AND CAST(substr(e.local_date, 9, 2) AS INTEGER) = @day");
            Add("@day", query.Day.Value);
        }

        if (query.Kind is not null)
        {
            where.Add(
                """
                CASE WHEN a.format IN ('mp4', 'mov') THEN 2
                     WHEN a.format IS NOT NULL THEN 1
                     ELSE 0 END = @kind
                """);
            Add("@kind", (int)query.Kind.Value);
        }

        if (query.FavoritesOnly)
        {
            where.Add("EXISTS (SELECT 1 FROM favorites f2 WHERE f2.memory_id = m.id)");
        }

        if (!query.IncludeExcluded)
        {
            where.Add("NOT EXISTS (SELECT 1 FROM exclusions x2 WHERE x2.memory_id = m.id)");
        }

        if (query.UndatedOnly)
        {
            where.Add("e.local_date IS NULL");
        }
        else if (!query.IncludeUndated)
        {
            where.Add("e.local_date IS NOT NULL");
        }

        if (query.OverlayOnly)
        {
            where.Add("m.status = 'overlayonly'");
        }

        if (query.WithLocationOnly)
        {
            where.Add("l.memory_id IS NOT NULL");
        }

        if (query.CollectionId is not null)
        {
            where.Add(
                "EXISTS (SELECT 1 FROM collection_items ci2 WHERE ci2.collection_id = @collection_id AND ci2.memory_id = m.id)");
            Add("@collection_id", query.CollectionId.Value);
        }

        if (query.PersonGroupId is not null)
        {
            where.Add(
                "EXISTS (SELECT 1 FROM faces fg WHERE fg.memory_id = m.id AND fg.group_id = @group_id)");
            Add("@group_id", query.PersonGroupId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            where.Add(
                """
                (
                    instr(lower(m.external_key), @search) > 0
                    OR instr(lower(COALESCE(n.text, '')), @search) > 0
                    OR instr(lower(COALESCE(ocr.text, '')), @search) > 0
                )
                """);
            Add("@search", query.SearchText.Trim().ToLowerInvariant());
        }

        if (extraWhere is not null)
        {
            where.AddRange(extraWhere);
        }

        if (extraParameters is not null)
        {
            parameters.AddRange(extraParameters);
        }

        var sql = DateCte + CardCtes + CardSelect;

        if (orderCollectionId is not null)
        {
            sql += "\nLEFT JOIN collection_items order_ci"
                   + "\n       ON order_ci.collection_id = @order_collection AND order_ci.memory_id = m.id";
            Add("@order_collection", orderCollectionId.Value);
        }

        sql += "\nLEFT JOIN memory_notes n ON n.memory_id = m.id"
               + "\nLEFT JOIN analysis_ocr ocr ON ocr.memory_id = m.id";

        if (where.Count > 0)
        {
            sql += "\nWHERE " + string.Join("\n  AND ", where);
        }

        sql += "\n" + orderClause;

        if (includePagination)
        {
            sql += "\nLIMIT @limit OFFSET @offset";
            Add("@limit", Math.Clamp(query.Limit <= 0 ? 500 : query.Limit, 1, 100_000));
            Add("@offset", Math.Max(0, query.Offset));
        }

        return new CardSql { Sql = sql, Parameters = parameters };
    }

    private async Task<List<MemoryCard>> ReadCardsAsync(
        SqliteConnection connection,
        string sql,
        IEnumerable<(string Name, object? Value)> parameters,
        CancellationToken ct)
    {
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
        var result = new List<MemoryCard>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var format = reader.IsDBNull(14) ? null : reader.GetString(14);
            long? mainAssetId = reader.IsDBNull(9) ? null : reader.GetInt64(9);
            long? overlayAssetId = reader.IsDBNull(10) ? null : reader.GetInt64(10);
            var thumbnail = reader.IsDBNull(22) ? null : reader.GetString(22);
            var hasThumbnail = thumbnail is not null &&
                               File.Exists(ImportPaths.ToPlatformPath(RootPath, thumbnail));
            // Un calque isolé ne prouve pas la présence d'une photo ou d'une vidéo.
            var kind = mainAssetId is null ? MediaKind.Unknown : KindForFormat(format);

            result.Add(new MemoryCard(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                kind,
                StatusFromText(reader.GetString(4)),
                DateText.ParseDate(reader.IsDBNull(5) ? null : reader.GetString(5)),
                (DateSource)Convert.ToInt32(reader.GetInt64(6), CultureInfo.InvariantCulture),
                (DatePrecision)Convert.ToInt32(reader.GetInt64(7), CultureInfo.InvariantCulture),
                (DateMeaning)Convert.ToInt32(reader.GetInt64(8), CultureInfo.InvariantCulture),
                mainAssetId,
                overlayAssetId,
                hasThumbnail,
                reader.GetInt64(17) != 0,
                reader.GetInt64(18) != 0,
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.IsDBNull(12) ? null : Convert.ToInt32(reader.GetInt64(12), CultureInfo.InvariantCulture),
                reader.IsDBNull(13) ? null : Convert.ToInt32(reader.GetInt64(13), CultureInfo.InvariantCulture),
                format,
                reader.IsDBNull(15) ? 0 : reader.GetInt64(15),
                Convert.ToInt32(reader.GetInt64(16), CultureInfo.InvariantCulture),
                reader.IsDBNull(19) ? null : reader.GetDouble(19),
                reader.IsDBNull(20) ? null : reader.GetDouble(20),
                reader.IsDBNull(21) ? null : reader.GetString(21)));
        }

        return result;
    }

    private async Task<List<MemoryCard>> RunCardQueryAsync(CardSql query, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ReadCardsAsync(connection, query.Sql, query.Parameters, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryCard>> QueryAsync(MemoryQuery query, CancellationToken ct = default)
    {
        var order = query.Descending
            ? "ORDER BY (e.local_date IS NULL) ASC, e.local_date DESC, e.local_time DESC, m.id DESC"
            : "ORDER BY (e.local_date IS NULL) ASC, e.local_date ASC, e.local_time ASC, m.id ASC";
        var sql = BuildCardSql(query, null, null, order, includePagination: true);
        return await RunCardQueryAsync(sql, ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(MemoryQuery query, CancellationToken ct = default)
    {
        var build = BuildCardSql(query, null, null, string.Empty, includePagination: false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM ({build.Sql}) AS matching;";
        foreach (var (name, paramValue) in build.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = paramValue ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var countValue = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(countValue, CultureInfo.InvariantCulture);
    }

    public async Task<MemoryCard?> GetCardAsync(long memoryId, CancellationToken ct = default)
    {
        var sql = BuildCardSql(
            new MemoryQuery { Limit = 1 },
            memoryId,
            null,
            "ORDER BY m.id",
            includePagination: false);
        var cards = await RunCardQueryAsync(sql, ct).ConfigureAwait(false);
        return cards.Count == 0 ? null : cards[0];
    }

    public async Task<IReadOnlyList<DateCandidateInfo>> GetDateCandidatesAsync(
        long memoryId,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            """
            SELECT id, raw, source, meaning, precision, timezone, instant_utc, local_date, confidence
              FROM date_candidates
             WHERE memory_id=@id
             ORDER BY CASE source WHEN 2 THEN 0 WHEN 3 THEN 1 WHEN 1 THEN 2 WHEN 4 THEN 3 ELSE 9 END,
                      confidence DESC,
                      id ASC;
            """,
            ("@id", memoryId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<DateCandidateInfo>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new DateCandidateInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                (DateSource)Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture),
                (DateMeaning)Convert.ToInt32(reader.GetInt64(3), CultureInfo.InvariantCulture),
                (DatePrecision)Convert.ToInt32(reader.GetInt64(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                DateText.ParseDate(reader.IsDBNull(7) ? null : reader.GetString(7)),
                reader.GetDouble(8)));
        }

        return result;
    }

    internal async Task<DateOverrideInfo?> GetDateOverrideAsync(long memoryId, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            "SELECT memory_id, local_date, precision, note, updated_at FROM date_overrides WHERE memory_id=@id;",
            ("@id", memoryId));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var localDate = DateText.ParseDate(reader.GetString(1))
                        ?? throw new InvalidDataException("Date de correction illisible dans le catalogue.");
        return new DateOverrideInfo(
            reader.GetInt64(0),
            localDate,
            (DatePrecision)Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task<MemoryDetails?> GetDetailsAsync(long memoryId, CancellationToken ct = default)
    {
        var card = await GetCardAsync(memoryId, ct).ConfigureAwait(false);
        if (card is null)
        {
            return null;
        }

        var candidates = await GetDateCandidatesAsync(memoryId, ct).ConfigureAwait(false);
        var overrideInfo = await GetDateOverrideAsync(memoryId, ct).ConfigureAwait(false);

        var sources = new List<SourceOccurrenceInfo>();
        var collections = new List<CollectionRef>();
        string? notes = null;

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using (var sourceCommand = CreateCommand(
                   connection,
                   """
                   SELECT sf.id, ar.source_path, sf.entry_path, sf.sha256, sf.size,
                          sf.declared_ext, sf.detected_format, sf.role
                     FROM memory_files mf
                     JOIN source_files sf ON sf.id=mf.source_file_id
                     LEFT JOIN archives ar ON ar.id=sf.archive_id
                    WHERE mf.memory_id=@id
                    ORDER BY sf.id;
                   """,
                   ("@id", memoryId)))
        {
            await using var reader = await sourceCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                sources.Add(new SourceOccurrenceInfo(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    RoleFromText(reader.GetString(7))));
            }
        }

        using (var collectionCommand = CreateCommand(
                   connection,
                   """
                   SELECT c.id, c.name, c.kind
                     FROM collection_items ci
                     JOIN collections c ON c.id=ci.collection_id
                    WHERE ci.memory_id=@id
                    ORDER BY ci.collection_id;
                   """,
                   ("@id", memoryId)))
        {
            await using var reader = await collectionCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                collections.Add(new CollectionRef(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    (CollectionKind)Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture)));
            }
        }

        var noteValue = await ScalarAsync(
            connection,
            "SELECT text FROM memory_notes WHERE memory_id=@id;",
            ct,
            ("@id", memoryId)).ConfigureAwait(false);
        notes = noteValue as string;

        return new MemoryDetails(card, candidates, overrideInfo, sources, collections, notes);
    }

    public async Task SetDateOverrideAsync(
        long memoryId,
        DateOnly? localDate,
        DatePrecision precision,
        string? note = null,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        if (localDate is null)
        {
            await ExecuteAsync(connection, "DELETE FROM date_overrides WHERE memory_id=@id;", ct, ("@id", memoryId))
                .ConfigureAwait(false);
        }
        else
        {
            var effectivePrecision = precision == DatePrecision.Unknown ? DatePrecision.Day : precision;
            await ExecuteAsync(
                connection,
                """
                INSERT INTO date_overrides (memory_id, local_date, precision, note, updated_at)
                VALUES (@id, @date, @precision, @note, @now)
                ON CONFLICT(memory_id) DO UPDATE SET
                    local_date=excluded.local_date,
                    precision=excluded.precision,
                    note=excluded.note,
                    updated_at=excluded.updated_at;
                """,
                ct,
                ("@id", memoryId),
                ("@date", DateText.Date(localDate.Value)),
                ("@precision", (int)effectivePrecision),
                ("@note", string.IsNullOrWhiteSpace(note) ? null : note.Trim()),
                ("@now", DateText.NowUtc())).ConfigureAwait(false);
        }

        await TouchMemoryAsync(connection, memoryId, ct).ConfigureAwait(false);
    }

    public async Task SetFavoriteAsync(
        IReadOnlyList<long> memoryIds,
        bool value,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var id in memoryIds.Distinct())
        {
            using var command = value
                ? CreateCommand(
                    connection,
                    "INSERT OR IGNORE INTO favorites (memory_id, created_at) VALUES (@id, @now);",
                    ("@id", id),
                    ("@now", DateText.NowUtc()))
                : CreateCommand(connection, "DELETE FROM favorites WHERE memory_id=@id;", ("@id", id));
            command.Transaction = (SqliteTransaction)transaction;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SetExcludedAsync(
        IReadOnlyList<long> memoryIds,
        bool value,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var id in memoryIds.Distinct())
        {
            using var command = value
                ? CreateCommand(
                    connection,
                    "INSERT OR IGNORE INTO exclusions (memory_id, scope, created_at) VALUES (@id, 'all', @now);",
                    ("@id", id),
                    ("@now", DateText.NowUtc()))
                : CreateCommand(connection, "DELETE FROM exclusions WHERE memory_id=@id;", ("@id", id));
            command.Transaction = (SqliteTransaction)transaction;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SetLocationAsync(
        long memoryId,
        double? latitude,
        double? longitude,
        string? label,
        CancellationToken ct = default)
    {
        if (latitude is null != longitude is null)
        {
            throw new ArgumentException("La latitude et la longitude doivent être fournies ensemble.");
        }

        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Coordonnées hors limites.");
        }

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        if (latitude is null)
        {
            await ExecuteAsync(connection, "DELETE FROM locations WHERE memory_id=@id;", ct, ("@id", memoryId))
                .ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO locations (memory_id, lat, lon, label, source, updated_at)
                VALUES (@id, @lat, @lon, @label, 'user', @now)
                ON CONFLICT(memory_id) DO UPDATE SET
                    lat=excluded.lat, lon=excluded.lon, label=excluded.label,
                    source=excluded.source, updated_at=excluded.updated_at;
                """,
                ct,
                ("@id", memoryId),
                ("@lat", latitude.Value),
                ("@lon", longitude!.Value),
                ("@label", string.IsNullOrWhiteSpace(label) ? null : label.Trim()),
                ("@now", DateText.NowUtc())).ConfigureAwait(false);
        }

        await TouchMemoryAsync(connection, memoryId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<YearSummary>> GetYearSummariesAsync(CancellationToken ct = default)
    {
        var sql = DateCte + """
            SELECT CAST(substr(e.local_date, 1, 4) AS INTEGER) AS year,
                   COUNT(*) AS item_count,
                   SUM(CASE WHEN EXISTS (
                       SELECT 1 FROM memory_files mf
                         JOIN source_files sf ON sf.id=mf.source_file_id
                         JOIN assets a ON a.sha256=sf.sha256
                        WHERE mf.memory_id=m.id AND mf.role='main'
                          AND a.format IN ('mp4', 'mov')) THEN 1 ELSE 0 END) AS videos,
                   SUM(CASE WHEN EXISTS (
                       SELECT 1 FROM memory_files mf
                         JOIN source_files sf ON sf.id=mf.source_file_id
                         JOIN assets a ON a.sha256=sf.sha256
                        WHERE mf.memory_id=m.id AND mf.role='main'
                          AND a.format NOT IN ('mp4', 'mov')) THEN 1 ELSE 0 END) AS photos,
                   MIN(e.local_date) AS first_date,
                   MAX(e.local_date) AS last_date
              FROM memories m
              JOIN effective_date e ON e.memory_id=m.id
             WHERE e.local_date IS NOT NULL
             GROUP BY year
             ORDER BY year DESC;
            """;
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<YearSummary>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new YearSummary(
                Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(3), CultureInfo.InvariantCulture),
                DateText.ParseDate(reader.IsDBNull(4) ? null : reader.GetString(4)),
                DateText.ParseDate(reader.IsDBNull(5) ? null : reader.GetString(5))));
        }

        return result;
    }

    public async Task<IReadOnlyList<MonthSummary>> GetMonthSummariesAsync(
        int? year = null,
        CancellationToken ct = default)
    {
        var sql = DateCte + """
            SELECT CAST(substr(e.local_date, 1, 4) AS INTEGER) AS year,
                   CAST(substr(e.local_date, 6, 2) AS INTEGER) AS month,
                   COUNT(*) AS item_count
              FROM memories m
              JOIN effective_date e ON e.memory_id=m.id
             WHERE e.local_date IS NOT NULL
            """;
        var parameters = new List<(string, object?)>();
        if (year is not null)
        {
            sql += " AND CAST(substr(e.local_date, 1, 4) AS INTEGER) = @year";
            parameters.Add(("@year", year.Value));
        }

        sql += " GROUP BY year, month ORDER BY year DESC, month DESC;";

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
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
        var result = new List<MonthSummary>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new MonthSummary(
                Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture)));
        }

        return result;
    }

    public async Task<DateOnly?> GetEarliestDateAsync(CancellationToken ct = default)
    {
        var sql = DateCte + " SELECT MIN(e.local_date) FROM effective_date e WHERE e.local_date IS NOT NULL;";
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var value = await ScalarAsync(connection, sql, ct).ConfigureAwait(false);
        return DateText.ParseDate(value as string);
    }

    public async Task<DateOnly?> GetLatestDateAsync(CancellationToken ct = default)
    {
        var sql = DateCte + " SELECT MAX(e.local_date) FROM effective_date e WHERE e.local_date IS NOT NULL;";
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var value = await ScalarAsync(connection, sql, ct).ConfigureAwait(false);
        return DateText.ParseDate(value as string);
    }

    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(
        CollectionKind? kind = null,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var baseSql = "SELECT id, name, kind, created_at FROM collections";
        var parameters = new List<(string, object?)>();
        if (kind is not null)
        {
            baseSql += " WHERE kind=@kind";
            parameters.Add(("@kind", (int)kind.Value));
        }

        baseSql += " ORDER BY name COLLATE NOCASE, id;";

        var collections = new List<(long Id, string Name, CollectionKind Kind, DateTimeOffset CreatedAt)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = baseSql;
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                collections.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    (CollectionKind)Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }
        }

        var itemsSql = DateCte + """
            SELECT ci.collection_id, ci.position, e.local_date, t.relative_path
              FROM collection_items ci
              JOIN memories m ON m.id=ci.memory_id
              LEFT JOIN effective_date e ON e.memory_id=ci.memory_id
              LEFT JOIN thumbnails t ON t.memory_id=ci.memory_id
             ORDER BY ci.collection_id, ci.position;
            """;

        var stats = new Dictionary<long, (int Count, string? Cover, DateOnly? Min, DateOnly? Max)>();
        using (var itemCommand = CreateCommand(connection, itemsSql))
        {
            await using var reader = await itemCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                var existing = stats.TryGetValue(id, out var current) ? current : (0, null, null, null);
                var date = DateText.ParseDate(reader.IsDBNull(2) ? null : reader.GetString(2));
                var cover = existing.Cover;
                if (cover is null && !reader.IsDBNull(3))
                {
                    cover = reader.GetString(3);
                }

                stats[id] = (
                    existing.Count + 1,
                    cover,
                    date is not null && (existing.Min is null || date < existing.Min) ? date : existing.Min,
                    date is not null && (existing.Max is null || date > existing.Max) ? date : existing.Max);
            }
        }

        return collections.Select(c =>
        {
            var stat = stats.TryGetValue(c.Id, out var value) ? value : (0, null, null, null);
            string? cover = null;
            if (stat.Item2 is not null)
            {
                var full = ImportPaths.ToPlatformPath(RootPath, stat.Item2);
                cover = File.Exists(full) ? full : null;
            }

            return new CollectionInfo(c.Id, c.Name, c.Kind, c.CreatedAt, stat.Item1, cover, stat.Item3, stat.Item4);
        }).ToList();
    }

    public async Task<CollectionInfo> CreateCollectionAsync(
        string name,
        CollectionKind kind,
        CancellationToken ct = default)
    {
        var cleanName = name?.Trim();
        if (string.IsNullOrEmpty(cleanName))
        {
            throw new ArgumentException("Le nom de la collection est obligatoire.", nameof(name));
        }

        var createdAt = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            "INSERT INTO collections (name, kind, created_at) VALUES (@name, @kind, @created) RETURNING id;",
            ("@name", cleanName),
            ("@kind", (int)kind),
            ("@created", DateText.Instant(createdAt)));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return new CollectionInfo(id, cleanName, kind, createdAt, 0, null, null, null);
    }

    public async Task RenameCollectionAsync(long collectionId, string name, CancellationToken ct = default)
    {
        var cleanName = name?.Trim();
        if (string.IsNullOrEmpty(cleanName))
        {
            throw new ArgumentException("Le nom de la collection est obligatoire.", nameof(name));
        }

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var affected = await ExecuteAsync(
            connection,
            "UPDATE collections SET name=@name WHERE id=@id;",
            ct,
            ("@name", cleanName),
            ("@id", collectionId)).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Collection {collectionId} introuvable.");
        }
    }

    public async Task DeleteCollectionAsync(long collectionId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(connection, "DELETE FROM collections WHERE id=@id;", ct, ("@id", collectionId))
            .ConfigureAwait(false);
    }

    public async Task AddToCollectionAsync(
        long collectionId,
        IReadOnlyList<long> memoryIds,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var position = await ScalarLongAsync(
            connection,
            "SELECT COALESCE(MAX(position), -1) + 1 FROM collection_items WHERE collection_id=@id;",
            ct,
            ("@id", collectionId)).ConfigureAwait(false);
        var exists = await ScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM collections WHERE id=@id;",
            ct,
            ("@id", collectionId)).ConfigureAwait(false);
        if (exists == 0)
        {
            throw new KeyNotFoundException($"Collection {collectionId} introuvable.");
        }

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var memoryId in memoryIds.Distinct())
        {
            using var command = CreateCommand(
                connection,
                """
                INSERT OR IGNORE INTO collection_items (collection_id, memory_id, position)
                VALUES (@collection, @memory, @position);
                """,
                ("@collection", collectionId),
                ("@memory", memoryId),
                ("@position", position));
            command.Transaction = (SqliteTransaction)transaction;
            var inserted = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (inserted > 0)
            {
                position++;
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveFromCollectionAsync(
        long collectionId,
        IReadOnlyList<long> memoryIds,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var memoryId in memoryIds.Distinct())
        {
            using var command = CreateCommand(
                connection,
                "DELETE FROM collection_items WHERE collection_id=@collection AND memory_id=@memory;",
                ("@collection", collectionId),
                ("@memory", memoryId));
            command.Transaction = (SqliteTransaction)transaction;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ReorderCollectionAsync(
        long collectionId,
        IReadOnlyList<long> orderedMemoryIds,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        for (var index = 0; index < orderedMemoryIds.Count; index++)
        {
            using var command = CreateCommand(
                connection,
                "UPDATE collection_items SET position=@position WHERE collection_id=@collection AND memory_id=@memory;",
                ("@position", index),
                ("@collection", collectionId),
                ("@memory", orderedMemoryIds[index]));
            command.Transaction = (SqliteTransaction)transaction;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryCard>> GetCollectionItemsAsync(
        long collectionId,
        CancellationToken ct = default)
    {
        var query = new MemoryQuery { CollectionId = collectionId, Limit = 100_000 };
        var sql = BuildCardSql(
            query,
            null,
            collectionId,
            "ORDER BY order_ci.position ASC, m.id ASC",
            includePagination: false);
        return await RunCardQueryAsync(sql, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<long>> GetRecentSuggestionIdsAsync(
        int days,
        CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, days));
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            """
            SELECT DISTINCT memory_id
              FROM suggestion_history
             WHERE proposed_at >= @since;
            """,
            ("@since", DateText.Instant(since)));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<long>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    public async Task RecordSuggestionAsync(
        IReadOnlyList<long> memoryIds,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var id in memoryIds.Distinct())
        {
            using var command = CreateCommand(
                connection,
                "INSERT INTO suggestion_history (memory_id, proposed_at) VALUES (@id, @now);",
                ("@id", id),
                ("@now", DateText.NowUtc()));
            command.Transaction = (SqliteTransaction)transaction;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}

