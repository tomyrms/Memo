using System.Globalization;
using System.Text.Json;

namespace Memo.Core;

public sealed class SettingsService : ISettingsService
{
    private readonly MemoCatalog _catalog;
    private readonly object _gate = new();
    private AppSettings _current;

    private SettingsService(MemoCatalog catalog, AppSettings current)
    {
        _catalog = catalog;
        _current = current;
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler? Changed;

    internal static async Task<SettingsService> LoadAsync(
        MemoCatalog catalog,
        string defaultTimeZone,
        CancellationToken ct)
    {
        var defaults = Defaults(defaultTimeZone);
        await using var connection = await catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        var raw = await MemoCatalog.ScalarAsync(
            connection,
            "SELECT value FROM settings WHERE key=@key;",
            ct,
            ("@key", SettingsStore.Key)).ConfigureAwait(false);

        AppSettings settings;
        if (raw is null)
        {
            settings = defaults;
            await SettingsStore.SaveAsync(connection, settings, ct).ConfigureAwait(false);
        }
        else
        {
            try
            {
                settings = SettingsStore.Deserialize(raw as string) ?? defaults;
                Validate(settings);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                // Des réglages illisibles ne doivent pas empêcher l'ouverture de la bibliothèque.
                settings = defaults;
            }
        }

        return new SettingsService(catalog, settings);
    }

    public static AppSettings Defaults(string timeZoneId) => new(
        timeZoneId,
        5,
        true,
        true,
        false,
        false,
        "09:00",
        true,
        false,
        "system",
        false,
        false);

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Validate(settings);
        await using var connection = await _catalog.OpenConnectionAsync(ct).ConfigureAwait(false);
        await SettingsStore.SaveAsync(connection, settings, ct).ConfigureAwait(false);

        bool changed;
        lock (_gate)
        {
            changed = !Equals(_current, settings);
            _current = settings;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void ApplyTimeZoneChange(string timeZoneId)
    {
        bool changed;
        lock (_gate)
        {
            if (string.Equals(_current.TimeZoneId, timeZoneId, StringComparison.Ordinal))
            {
                return;
            }

            _current = _current with { TimeZoneId = timeZoneId };
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static void Validate(AppSettings settings)
    {
        TimeZoneHelper.Resolve(settings.TimeZoneId);
        if (settings.PhotoDurationSeconds is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "La durée d'affichage d'une photo doit être comprise entre 1 et 120 secondes.");
        }

        if (!TimeOnly.TryParseExact(
                settings.ReminderTime,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw new ArgumentException("L'heure de rappel doit être au format HH:mm.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Theme))
        {
            throw new ArgumentException("Le thème ne peut pas être vide.", nameof(settings));
        }
    }
}

internal static class SettingsStore
{
    internal const string Key = "app";

    internal static AppSettings? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonSupport.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Les réglages enregistrés dans le catalogue sont illisibles.", ex);
        }
    }

    internal static async Task SaveAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        AppSettings settings,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(settings, JsonSupport.Compact);
        await MemoCatalog.ExecuteAsync(
            connection,
            """
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """,
            ct,
            ("@key", Key),
            ("@value", json)).ConfigureAwait(false);
    }
}
