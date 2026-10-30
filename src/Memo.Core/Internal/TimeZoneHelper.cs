namespace Memo.Core;

internal static class TimeZoneHelper
{
    public static TimeZoneInfo Resolve(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("Le fuseau horaire est obligatoire.", nameof(timeZoneId));
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new ArgumentException($"Fuseau horaire inconnu : « {timeZoneId} ».", nameof(timeZoneId), ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new ArgumentException($"Fuseau horaire invalide : « {timeZoneId} ».", nameof(timeZoneId), ex);
        }
    }
}
