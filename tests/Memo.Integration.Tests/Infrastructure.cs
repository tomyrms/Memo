using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Memo.Core;
using Memo.TestSupport;

namespace Memo.Integration.Tests;

/// <summary>
/// Base des tests d'intégration : chaque test possède son propre dossier
/// temporaire, nettoyé au teardown, et crée des bibliothèques indépendantes.
/// </summary>
[TestClass]
public abstract class IntegrationTestBase
{
    private TempLibrary _temp = null!;

    protected string Root => _temp.Root;

    [TestInitialize]
    public void Initialize() => _temp = new TempLibrary("memo-integration-");

    [TestCleanup]
    public void Cleanup() => _temp.Dispose();

    protected Task<MemoLibrary> CreateLibraryAsync(
        string timeZoneId = "Europe/Zurich",
        string name = "Bibliothèque d'intégration")
    {
        var root = Path.Combine(Root, "library-" + Guid.NewGuid().ToString("N")[..10]);
        return MemoLibrary.CreateAsync(root, name, timeZoneId);
    }

    protected static Task<SpaceInfo> CreateSpaceAsync(
        MemoLibrary library,
        string name = "Compte principal") =>
        library.Catalog.CreateSpaceAsync(name);

    protected static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new AssertFailedException(
                $"Exception {exception.GetType().Name} au lieu de {typeof(TException).Name} : {exception.Message}");
        }

        throw new AssertFailedException($"Aucune exception {typeof(TException).Name} n'a été levée.");
    }
}

/// <summary>Identifiants UUID v4 déterministes, acceptés par le format des exports.</summary>
internal static class IntegrationIds
{
    public static Guid Sequential(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return Guid.Parse($"{value:x8}-0000-4000-8000-{value:x12}");
    }
}

/// <summary>Fabrique de souvenirs synthétiques sans média réel.</summary>
internal static class SyntheticMedia
{
    public static SyntheticMemory Photo(
        int id,
        string? caption = null,
        int width = 320,
        int height = 240,
        int seed = 0,
        SyntheticImageFormat format = SyntheticImageFormat.Jpeg)
    {
        var bytes = SyntheticImage.Photo(
            "2021-07-14",
            caption ?? $"SOUVENIR {id:000}",
            format,
            width,
            height,
            seed);
        return new SyntheticMemory
        {
            Id = IntegrationIds.Sequential(id),
            Kind = SyntheticMemoryKind.Photo,
            MainBytes = bytes,
            MainExtension = format switch
            {
                SyntheticImageFormat.Jpeg => "jpg",
                SyntheticImageFormat.Png => "png",
                SyntheticImageFormat.Webp => "webp",
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            },
        };
    }

    public static SyntheticMemory Video(
        int id,
        DateTimeOffset creationTimeUtc,
        int rotation = 0,
        int durationMs = 4000)
    {
        return new SyntheticMemory
        {
            Id = IntegrationIds.Sequential(id),
            Kind = SyntheticMemoryKind.Video,
            MainBytes = SyntheticMp4.Create(creationTimeUtc, rotation, 1920, 1080, durationMs),
            MainExtension = "mp4",
        };
    }

    public static SyntheticMemory WithOverlay(
        this SyntheticMemory memory,
        int width = 320,
        int height = 240,
        string? caption = null,
        SyntheticImageFormat format = SyntheticImageFormat.Webp,
        int seed = 0,
        double scale = 1d)
    {
        return memory with
        {
            OverlayBytes = SyntheticImage.Overlay(
                width,
                height,
                scale,
                format,
                caption ?? "CALQUE",
                seed),
            OverlayExtension = format switch
            {
                SyntheticImageFormat.Jpeg => "jpg",
                SyntheticImageFormat.Png => "png",
                SyntheticImageFormat.Webp => "webp",
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            },
        };
    }
}

/// <summary>Écrit les lots synthétiques utilisés par les scénarios d'ordre.</summary>
internal static class ArchiveBuilder
{
    private static readonly DateTimeOffset NeutralZipTime =
        new(2021, 7, 14, 12, 0, 0, TimeSpan.Zero);

    public static string WriteMains(
        string path,
        IEnumerable<SyntheticMemory> memories,
        DateTimeOffset? zipTime = null) =>
        ExportBuilder.Write(path, memories, new ExportArchiveOptions
        {
            Roles = SyntheticRoleFilter.Main,
            IncludeIndexHtml = false,
            IncludeJson = false,
            ZipTimestamp = zipTime ?? NeutralZipTime,
        });

    public static string WriteOverlays(
        string path,
        IEnumerable<SyntheticMemory> memories,
        DateTimeOffset? zipTime = null) =>
        ExportBuilder.Write(path, memories, new ExportArchiveOptions
        {
            Roles = SyntheticRoleFilter.Overlay,
            IncludeIndexHtml = false,
            IncludeJson = false,
            ZipTimestamp = zipTime ?? NeutralZipTime,
        });

    public static string WriteMetadata(
        string path,
        IEnumerable<SyntheticMemory> memories,
        DateTimeOffset? zipTime = null) =>
        ExportBuilder.Write(path, memories, new ExportArchiveOptions
        {
            Roles = SyntheticRoleFilter.None,
            IncludeIndexHtml = true,
            IncludeJson = true,
            ZipTimestamp = zipTime ?? NeutralZipTime,
        });

    public static string WriteComplete(
        string path,
        IEnumerable<SyntheticMemory> memories,
        DateTimeOffset? zipTime = null) =>
        ExportBuilder.Write(path, memories, new ExportArchiveOptions
        {
            Roles = SyntheticRoleFilter.Both,
            IncludeIndexHtml = true,
            IncludeJson = true,
            ZipTimestamp = zipTime ?? NeutralZipTime,
        });
}

/// <summary>
/// Sérialise l'état observable d'une bibliothèque. Les identifiants internes
/// sont optionnels : ils diffèrent légitimement selon l'ordre d'insertion mais
/// doivent être identiques après une sauvegarde/restauration.
/// </summary>
internal static class LibrarySnapshot
{
    public static async Task<string> MemoriesAsync(MemoLibrary library, bool includeIdentity = false)
    {
        var builder = new StringBuilder();
        var cards = await library.Catalog.QueryAsync(new MemoryQuery { Limit = 100_000 });
        foreach (var card in cards
                     .OrderBy(item => item.ExternalKey, StringComparer.Ordinal)
                     .ThenBy(item => item.SpaceName, StringComparer.Ordinal))
        {
            var main = await AssetAsync(library.Catalog, card.MainAssetId, includeIdentity);
            var overlay = await AssetAsync(library.Catalog, card.OverlayAssetId, includeIdentity);
            builder.AppendLine(string.Join(
                '|',
                "CARD",
                Identity(card.Id, includeIdentity),
                Identity(card.SpaceId, includeIdentity),
                card.SpaceName,
                card.ExternalKey,
                card.Kind,
                card.Status,
                Date(card.LocalDate),
                card.DateSource,
                card.DatePrecision,
                card.DateMeaning,
                main,
                overlay,
                card.HasThumbnail,
                card.IsFavorite,
                card.IsExcluded,
                Number(card.DurationMs),
                Number(card.Width),
                Number(card.Height),
                card.Format ?? "-",
                card.SizeBytes,
                card.OverlayCount,
                Number(card.Latitude),
                Number(card.Longitude),
                card.LocationLabel ?? "-"));

            var details = await library.Catalog.GetDetailsAsync(card.Id)
                          ?? throw new AssertFailedException($"Détails absents pour le souvenir {card.Id}.");
            foreach (var candidate in details.DateCandidates
                         .OrderBy(item => item.Source)
                         .ThenBy(item => item.RawValue, StringComparer.Ordinal)
                         .ThenBy(item => item.Id))
            {
                builder.AppendLine(string.Join(
                    '|',
                    "DATE",
                    Identity(candidate.Id, includeIdentity),
                    candidate.RawValue,
                    candidate.Source,
                    candidate.Meaning,
                    candidate.Precision,
                    candidate.TimeZoneId ?? "-",
                    candidate.InstantUtc ?? "-",
                    Date(candidate.LocalDate),
                    Number(candidate.Confidence)));
            }

            if (details.Override is { } dateOverride)
            {
                builder.AppendLine(string.Join(
                    '|',
                    "OVERRIDE",
                    Identity(dateOverride.MemoryId, includeIdentity),
                    Date(dateOverride.LocalDate),
                    dateOverride.Precision,
                    dateOverride.Note ?? "-",
                    Instant(dateOverride.UpdatedAt)));
            }
            else
            {
                builder.AppendLine("OVERRIDE|-");
            }

            foreach (var source in details.Sources
                         .OrderBy(item => item.Role)
                         .ThenBy(item => item.EntryPath, StringComparer.Ordinal)
                         .ThenBy(item => item.FileId))
            {
                builder.AppendLine(string.Join(
                    '|',
                    "SOURCE",
                    Identity(source.FileId, includeIdentity),
                    source.ArchivePath,
                    source.EntryPath,
                    source.Sha256,
                    source.Size,
                    source.DeclaredExtension,
                    source.DetectedFormat,
                    source.Role));
            }

            foreach (var collection in details.Collections.OrderBy(item => item.Id))
            {
                builder.AppendLine(string.Join(
                    '|',
                    "MEMORY-COLLECTION",
                    Identity(collection.Id, includeIdentity),
                    collection.Name,
                    collection.Kind));
            }

            builder.AppendLine($"NOTES|{details.Notes ?? "-"}");
        }

        return builder.ToString();
    }

    public static async Task<string> FullAsync(MemoLibrary library)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(
            '|',
            "LIBRARY",
            library.Info.Id,
            library.Info.Name,
            library.Info.TimeZoneId,
            Instant(library.Info.CreatedAt)));

        foreach (var space in (await library.Catalog.GetSpacesAsync()).OrderBy(item => item.Id))
        {
            builder.AppendLine(string.Join(
                '|',
                "SPACE",
                space.Id,
                space.Name,
                space.AccountHint ?? "-",
                Instant(space.CreatedAt)));
        }

        builder.Append(await MemoriesAsync(library, includeIdentity: true));

        foreach (var collection in (await library.Catalog.GetCollectionsAsync())
                     .OrderBy(item => item.Id))
        {
            builder.AppendLine(string.Join(
                '|',
                "COLLECTION",
                collection.Id,
                collection.Name,
                collection.Kind,
                Instant(collection.CreatedAt),
                collection.ItemCount,
                collection.CoverThumbnail ?? "-",
                Date(collection.FirstDate),
                Date(collection.LastDate)));
            foreach (var item in await library.Catalog.GetCollectionItemsAsync(collection.Id))
            {
                builder.AppendLine($"COLLECTION-ITEM|{collection.Id}|{item.Id}|{item.ExternalKey}");
            }
        }

        var settings = library.Settings.Current;
        builder.AppendLine(string.Join(
            '|',
            "SETTINGS",
            settings.TimeZoneId,
            settings.PhotoDurationSeconds,
            settings.AutoplayVideos,
            settings.PlayAudio,
            settings.ReduceMotion,
            settings.RemindersEnabled,
            settings.ReminderTime,
            settings.DiscreetNotifications,
            settings.IncludeUncertainDatesInRecaps,
            settings.Theme,
            settings.OcrEnabled,
            settings.FacesEnabled));

        var stats = await library.Catalog.GetStorageStatsAsync();
        builder.AppendLine(string.Join(
            '|',
            "STATS",
            stats.Memories,
            stats.Assets,
            stats.Sources,
            stats.SourceBytes,
            stats.DerivedBytes,
            stats.Spaces));

        foreach (var batch in (await library.Catalog.GetBatchesAsync()).OrderBy(item => item.Id))
        {
            builder.AppendLine(string.Join(
                '|',
                "BATCH",
                batch.Id,
                batch.SpaceId,
                batch.SpaceName,
                Instant(batch.StartedAt),
                batch.CompletedAt is null ? "-" : Instant(batch.CompletedAt.Value),
                batch.Status,
                batch.Archives,
                batch.Memories,
                batch.Note ?? "-"));
        }

        builder.AppendLine($"EARLIEST|{Date(await library.Catalog.GetEarliestDateAsync())}");
        builder.AppendLine($"LATEST|{Date(await library.Catalog.GetLatestDateAsync())}");
        foreach (var year in await library.Catalog.GetYearSummariesAsync())
        {
            builder.AppendLine(string.Join(
                '|',
                "YEAR",
                year.Year,
                year.Count,
                year.Videos,
                year.Photos,
                Date(year.FirstDate),
                Date(year.LastDate)));
        }

        foreach (var month in await library.Catalog.GetMonthSummariesAsync())
        {
            builder.AppendLine($"MONTH|{month.Year}|{month.Month}|{month.Count}");
        }

        await AppendFilesAsync(builder, library.Info.RootPath);
        return builder.ToString();
    }

    private static async Task<string> AssetAsync(IMemoCatalog catalog, long? assetId, bool includeIdentity)
    {
        if (assetId is null)
        {
            return "-";
        }

        var path = catalog.GetAssetPath(assetId.Value);
        if (!File.Exists(path))
        {
            return "MISSING";
        }

        var relative = Path.GetRelativePath(catalog.RootPath, path).Replace('\\', '/');
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)))
            .ToLowerInvariant();
        return includeIdentity ? $"{assetId.Value}:{relative}:{hash}" : $"{relative}:{hash}";
    }

    private static async Task AppendFilesAsync(StringBuilder builder, string root)
    {
        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                FullPath = path,
                Relative = Path.GetRelativePath(root, path).Replace('\\', '/'),
            })
            .Where(item =>
                !item.Relative.StartsWith("imports/", StringComparison.Ordinal) &&
                !item.Relative.StartsWith("derived/", StringComparison.Ordinal) &&
                !item.Relative.StartsWith("catalog.db", StringComparison.Ordinal) &&
                !string.Equals(item.Relative, "manifest.json", StringComparison.Ordinal))
            .OrderBy(item => item.Relative, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var content = await File.ReadAllBytesAsync(file.FullPath);
            var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            builder.AppendLine($"FILE|{file.Relative}|{content.LongLength}|{hash}");
        }
    }

    private static string Identity(long value, bool include) => include ? value.ToString(CultureInfo.InvariantCulture) : "-";

    private static string Date(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";

    private static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Number(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Number(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "-";
}
