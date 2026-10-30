using System.Globalization;

namespace Memo.Core;

internal static class DateText
{
    public const string DateFormat = "yyyy-MM-dd";
    public const string TimeFormat = "HH:mm:ss";

    public static string Date(DateOnly value) =>
        value.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string? Date(DateOnly? value) => value is null ? null : Date(value.Value);

    public static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    public static string Time(TimeOnly value) =>
        value.ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static string? Time(TimeOnly? value) => value is null ? null : Time(value.Value);

    public static TimeOnly? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TimeOnly.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : null;
    }

    // Les instants sont stockés en UTC avec le suffixe Z, sans dépendre du fuseau de la machine.
    public static string Instant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out var instant)
            ? instant.ToUniversalTime()
            : null;
    }

    public static (DateOnly Date, TimeOnly Time) Local(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, timeZone);
        return (DateOnly.FromDateTime(local.DateTime), TimeOnly.FromDateTime(local.DateTime));
    }

    public static DateOnly Today(TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).DateTime);

    public static string NowUtc() => DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
