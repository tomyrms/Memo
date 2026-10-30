namespace Memo.Core;

public enum MediaKind { Unknown = 0, Photo = 1, Video = 2 }

public enum MediaRole { Main = 1, Overlay = 2 }

public enum MemoryStatus { Unknown = 0, Complete = 1, MediaOnly = 2, OverlayOnly = 3, Conflict = 4 }

public enum DateSource { Unknown = 0, Mp4CreationTime = 1, ExifDateTimeOriginal = 2, ExifDateTime = 3, Index = 4, User = 5 }

public enum DatePrecision { Unknown = 0, Year = 1, Month = 2, Day = 3, Second = 4 }

public enum DateMeaning { Unknown = 0, ContainerCreated = 1, Capture = 2, Export = 3, UserProvided = 4 }

public enum ImportPhase { Scanning, Reading, Storing, Associating, Analyzing, Done, Failed, Cancelled }

public enum CollectionKind { Album = 1, Recap = 2, Event = 3 }

public sealed record SpaceInfo(long Id, string Name, string? AccountHint, DateTimeOffset CreatedAt);

public sealed record LibraryInfo(Guid Id, string Name, string RootPath, string TimeZoneId, DateTimeOffset CreatedAt);

public sealed record MediaInfo(
    string Format,
    int? Width,
    int? Height,
    int Rotation,
    long? DurationMs,
    bool HasAudio,
    DateTimeOffset? CreationTimeUtc,
    DateOnly? ExifDate,
    string? ExifDateRaw);

public sealed record DateCandidateInfo(
    long Id,
    string RawValue,
    DateSource Source,
    DateMeaning Meaning,
    DatePrecision Precision,
    string? TimeZoneId,
    string? InstantUtc,
    DateOnly? LocalDate,
    double Confidence);

public sealed record DateOverrideInfo(long MemoryId, DateOnly LocalDate, DatePrecision Precision, string? Note, DateTimeOffset UpdatedAt);

public sealed record SourceOccurrenceInfo(long FileId, string? ArchivePath, string EntryPath, string Sha256, long Size, string DeclaredExtension, string DetectedFormat, MediaRole Role);

public sealed record MemoryCard(
    long Id,
    long SpaceId,
    string SpaceName,
    string ExternalKey,
    MediaKind Kind,
    MemoryStatus Status,
    DateOnly? LocalDate,
    DateSource DateSource,
    DatePrecision DatePrecision,
    DateMeaning DateMeaning,
    long? MainAssetId,
    long? OverlayAssetId,
    bool HasThumbnail,
    bool IsFavorite,
    bool IsExcluded,
    long? DurationMs,
    int? Width,
    int? Height,
    string? Format,
    long SizeBytes,
    int OverlayCount,
    double? Latitude,
    double? Longitude,
    string? LocationLabel);

public sealed record MemoryDetails(
    MemoryCard Card,
    IReadOnlyList<DateCandidateInfo> DateCandidates,
    DateOverrideInfo? Override,
    IReadOnlyList<SourceOccurrenceInfo> Sources,
    IReadOnlyList<CollectionRef> Collections,
    string? Notes);

public sealed record CollectionRef(long Id, string Name, CollectionKind Kind);

public sealed record CollectionInfo(long Id, string Name, CollectionKind Kind, DateTimeOffset CreatedAt, int ItemCount, string? CoverThumbnail, DateOnly? FirstDate, DateOnly? LastDate);

public sealed record YearSummary(int Year, int Count, int Videos, int Photos, DateOnly? FirstDate, DateOnly? LastDate);

public sealed record MonthSummary(int Year, int Month, int Count);

public sealed record ImportDiagnostic(string Severity, string Code, string Message, string? Path = null, string? EntryPath = null);

public sealed record ImportCounters(
    int ArchivesSeen,
    int ArchivesImported,
    int EntriesSeen,
    int MediaImported,
    int OverlaysImported,
    int MemoriesCreated,
    int MemoriesUpdated,
    int DuplicatesSkipped,
    int Unrecognized,
    int Errors)
{
    public static readonly ImportCounters Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record ImportProgress(
    ImportPhase Phase,
    string Message,
    int FilesProcessed,
    int FilesTotal,
    string? CurrentItem,
    ImportCounters Counters);

public sealed record ImportRequest(
    long SpaceId,
    IReadOnlyList<string> Paths,
    bool IncludeChatMedia = false,
    string? BatchNote = null);

public sealed record ImportResult(
    long BatchId,
    ImportCounters Counters,
    IReadOnlyList<ImportDiagnostic> Diagnostics,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Cancelled);

public sealed record ThisDayResult(DateOnly Day, IReadOnlyList<MemoryCard> Items, IReadOnlyList<int> Years, int UndatedCount);

public sealed record SurpriseResult(IReadOnlyList<MemoryCard> Items, DateOnly? Day, string Label);

public sealed record MemoryQuery(
    long? SpaceId = null,
    int? Year = null,
    int? Month = null,
    int? Day = null,
    MediaKind? Kind = null,
    bool FavoritesOnly = false,
    bool IncludeExcluded = true,
    bool IncludeUndated = true,
    bool UndatedOnly = false,
    bool OverlayOnly = false,
    bool WithLocationOnly = false,
    long? CollectionId = null,
    long? PersonGroupId = null,
    string? SearchText = null,
    bool Descending = true,
    int Offset = 0,
    int Limit = 500);

public sealed record ThisDayOptions(
    bool IncludeUncertainDates = true,
    bool IncludeMediaOnly = true,
    int MaxYears = 10);

public sealed record AppSettings(
    string TimeZoneId,
    int PhotoDurationSeconds,
    bool AutoplayVideos,
    bool PlayAudio,
    bool ReduceMotion,
    bool RemindersEnabled,
    string ReminderTime,
    bool DiscreetNotifications,
    bool IncludeUncertainDatesInRecaps,
    string Theme,
    bool OcrEnabled,
    bool FacesEnabled);

public sealed record BackupManifest(
    int Version,
    Guid LibraryId,
    string LibraryName,
    DateTimeOffset CreatedAt,
    int MemoryCount,
    int SourceCount,
    long TotalBytes,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> Excluded);

public sealed record BackupResult(string Path, BackupManifest Manifest);
