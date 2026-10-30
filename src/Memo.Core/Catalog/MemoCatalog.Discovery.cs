using System.Globalization;
using System.Text.Json;

namespace Memo.Core;

internal sealed record DatedDayInfo(DateOnly Day, int Total, int NonRecent);

public sealed partial class MemoCatalog
{
    internal async Task<ThisDayResult> QueryThisDayAsync(
        DateOnly day,
        ThisDayOptions options,
        CancellationToken ct)
    {
        var timeZone = TimeZoneHelper.Resolve(TimeZoneId);
        var today = DateText.Today(timeZone);
        // Un anniversaire ne concerne que les années antérieures. Une date passée explicitement
        // fournie par l'appelant inclut son année, mais jamais les années postérieures.
        var maxYear = day.Year < today.Year ? day.Year : today.Year - 1;
        var monthDay = $"{day.Month:00}-{day.Day:00}";

        var filters = new List<string>
        {
            "e.local_date IS NOT NULL",
            // Une date connue seulement au mois ou à l'année ne peut pas déclencher un anniversaire exact.
            "e.date_precision >= 3",
            "substr(e.local_date, 6, 5) = @month_day",
            "CAST(substr(e.local_date, 1, 4) AS INTEGER) <= @max_year",
            "m.status <> 'overlayonly'",
            "NOT EXISTS (SELECT 1 FROM exclusions x3 WHERE x3.memory_id = m.id)",
        };
        var parameters = new List<(string Name, object? Value)>
        {
            ("@month_day", monthDay),
            ("@max_year", maxYear),
        };

        if (!options.IncludeUncertainDates)
        {
            filters.Add("e.date_confidence >= 0.9");
        }

        if (!options.IncludeMediaOnly)
        {
            filters.Add("m.status = 'complete'");
        }

        var yearsSql = DateCte
                       + "SELECT DISTINCT CAST(substr(e.local_date, 1, 4) AS INTEGER) AS year"
                       + " FROM memories m JOIN effective_date e ON e.memory_id = m.id"
                       + " WHERE " + string.Join(" AND ", filters)
                       + " ORDER BY year DESC;";

        var allYears = new List<int>();
        await using (var connection = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            using var command = connection.CreateCommand();
            command.CommandText = yearsSql;
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
                allYears.Add(Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture));
            }
        }

        var selectedYears = options.MaxYears > 0
            ? allYears.Take(options.MaxYears).ToList()
            : allYears;

        var cards = new List<MemoryCard>();
        if (selectedYears.Count > 0)
        {
            var yearParameters = selectedYears
                .Select((year, index) => ($"@year_{index}", (object?)year))
                .ToList();
            filters.Add($"CAST(substr(e.local_date, 1, 4) AS INTEGER) IN ({string.Join(", ", yearParameters.Select(p => p.Item1))})");
            parameters.AddRange(yearParameters);
            var sql = BuildCardSql(
                new MemoryQuery { Limit = 100_000 },
                null,
                null,
                "ORDER BY e.local_date DESC, e.local_time DESC, m.id DESC",
                includePagination: false,
                extraWhere: filters,
                extraParameters: parameters);
            cards = await RunCardQueryAsync(sql, ct).ConfigureAwait(false);
        }

        var undated = await CountUndatedAsync(ct).ConfigureAwait(false);
        var years = cards
            .Where(card => card.LocalDate is not null)
            .Select(card => card.LocalDate!.Value.Year)
            .Distinct()
            .OrderByDescending(year => year)
            .ToList();

        return new ThisDayResult(day, cards, years, undated);
    }

    internal async Task<int> CountUndatedAsync(CancellationToken ct)
    {
        var sql = DateCte + """
            SELECT COUNT(*)
              FROM memories m
              JOIN effective_date e ON e.memory_id = m.id
             WHERE e.local_date IS NULL
               AND NOT EXISTS (SELECT 1 FROM exclusions x WHERE x.memory_id = m.id);
            """;
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var value = await ScalarLongAsync(connection, sql, ct).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    internal async Task<IReadOnlyList<DatedDayInfo>> GetDatedDaysAsync(
        IReadOnlyCollection<long> excludedIds,
        CancellationToken ct)
    {
        var excludedJson = JsonSerializer.Serialize(excludedIds.Distinct().ToList(), JsonSupport.Compact);
        var since = DateText.Instant(DateTimeOffset.UtcNow.AddDays(-14));
        var sql = DateCte + """
            SELECT e.local_date,
                   COUNT(*) AS total,
                   SUM(CASE WHEN EXISTS (
                       SELECT 1 FROM suggestion_history sh
                        WHERE sh.memory_id = m.id AND sh.proposed_at >= @since) THEN 0 ELSE 1 END) AS fresh
              FROM memories m
              JOIN effective_date e ON e.memory_id = m.id
             WHERE e.local_date IS NOT NULL
               AND e.date_precision >= 3
               AND m.status <> 'overlayonly'
               AND NOT EXISTS (SELECT 1 FROM exclusions x WHERE x.memory_id = m.id)
               AND m.id NOT IN (SELECT value FROM json_each(@excluded))
             GROUP BY e.local_date
             ORDER BY e.local_date DESC;
            """;
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = CreateCommand(
            connection,
            sql,
            ("@since", since),
            ("@excluded", excludedJson));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<DatedDayInfo>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var date = DateText.ParseDate(reader.GetString(0));
            if (date is null)
            {
                continue;
            }

            result.Add(new DatedDayInfo(
                date.Value,
                Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture)));
        }

        return result;
    }

    internal async Task<IReadOnlyList<MemoryCard>> GetDayCardsAsync(
        DateOnly day,
        IReadOnlyCollection<long> excludedIds,
        CancellationToken ct)
    {
        var excludedJson = JsonSerializer.Serialize(excludedIds.Distinct().ToList(), JsonSupport.Compact);
        var filters = new List<string>
        {
            "e.local_date = @day",
            "e.date_precision >= 3",
            "m.status <> 'overlayonly'",
            "NOT EXISTS (SELECT 1 FROM exclusions x4 WHERE x4.memory_id = m.id)",
            "m.id NOT IN (SELECT value FROM json_each(@excluded))",
        };
        var parameters = new List<(string Name, object? Value)>
        {
            ("@day", DateText.Date(day)),
            ("@excluded", excludedJson),
        };

        var sql = BuildCardSql(
            new MemoryQuery { Limit = 100_000 },
            null,
            null,
            "ORDER BY e.local_time ASC, m.id ASC",
            includePagination: false,
            extraWhere: filters,
            extraParameters: parameters);
        return await RunCardQueryAsync(sql, ct).ConfigureAwait(false);
    }
}
