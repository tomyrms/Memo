namespace Memo.Core;

public interface IMemoCatalog
{
    LibraryInfo Info { get; }
    string RootPath { get; }
    string TimeZoneId { get; }

    string GetAssetPath(long assetId);
    string? GetThumbnailPath(long memoryId);
    string? GetCompositePath(long memoryId);

    Task<IReadOnlyList<SpaceInfo>> GetSpacesAsync(CancellationToken ct = default);
    Task<SpaceInfo> CreateSpaceAsync(string name, string? accountHint = null, CancellationToken ct = default);
    Task RenameSpaceAsync(long spaceId, string name, CancellationToken ct = default);

    Task<IReadOnlyList<MemoryCard>> QueryAsync(MemoryQuery query, CancellationToken ct = default);
    Task<int> CountAsync(MemoryQuery query, CancellationToken ct = default);
    Task<MemoryCard?> GetCardAsync(long memoryId, CancellationToken ct = default);
    Task<MemoryDetails?> GetDetailsAsync(long memoryId, CancellationToken ct = default);
    Task<MediaInfo?> GetMediaInfoAsync(long memoryId, MediaRole role, CancellationToken ct = default);
    Task<IReadOnlyList<DateCandidateInfo>> GetDateCandidatesAsync(long memoryId, CancellationToken ct = default);

    Task SetFavoriteAsync(IReadOnlyList<long> memoryIds, bool value, CancellationToken ct = default);
    Task SetExcludedAsync(IReadOnlyList<long> memoryIds, bool value, CancellationToken ct = default);
    Task SetDateOverrideAsync(long memoryId, DateOnly? localDate, DatePrecision precision, string? note = null, CancellationToken ct = default);
    Task SetLocationAsync(long memoryId, double? latitude, double? longitude, string? label, CancellationToken ct = default);
    Task SetThumbnailAsync(long memoryId, string? relativePath, CancellationToken ct = default);
    Task RecalculateClassificationAsync(string timeZoneId, CancellationToken ct = default);

    Task<IReadOnlyList<YearSummary>> GetYearSummariesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MonthSummary>> GetMonthSummariesAsync(int? year = null, CancellationToken ct = default);
    Task<DateOnly?> GetEarliestDateAsync(CancellationToken ct = default);
    Task<DateOnly?> GetLatestDateAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CollectionKind? kind = null, CancellationToken ct = default);
    Task<CollectionInfo> CreateCollectionAsync(string name, CollectionKind kind, CancellationToken ct = default);
    Task RenameCollectionAsync(long collectionId, string name, CancellationToken ct = default);
    Task DeleteCollectionAsync(long collectionId, CancellationToken ct = default);
    Task AddToCollectionAsync(long collectionId, IReadOnlyList<long> memoryIds, CancellationToken ct = default);
    Task RemoveFromCollectionAsync(long collectionId, IReadOnlyList<long> memoryIds, CancellationToken ct = default);
    Task ReorderCollectionAsync(long collectionId, IReadOnlyList<long> orderedMemoryIds, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryCard>> GetCollectionItemsAsync(long collectionId, CancellationToken ct = default);

    Task<IReadOnlyList<long>> GetRecentSuggestionIdsAsync(int days, CancellationToken ct = default);
    Task RecordSuggestionAsync(IReadOnlyList<long> memoryIds, CancellationToken ct = default);

    Task<IReadOnlyList<ImportBatchInfo>> GetBatchesAsync(CancellationToken ct = default);
    Task<StorageStats> GetStorageStatsAsync(CancellationToken ct = default);
    Task VacuumAsync(CancellationToken ct = default);
}

public sealed record ImportBatchInfo(long Id, long SpaceId, string SpaceName, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string Status, int Archives, int Memories, string? Note);

public sealed record StorageStats(int Memories, int Assets, int Sources, long SourceBytes, long DerivedBytes, int Spaces);

public interface IImportService
{
    Task<ImportResult> RunAsync(ImportRequest request, IProgress<ImportProgress>? progress = null, CancellationToken ct = default);
    Task<int> InspectAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
}

public interface IBackupService
{
    Task<BackupResult> CreateBackupAsync(string destinationZip, IProgress<string>? progress = null, CancellationToken ct = default);
    Task RestoreAsync(string backupZip, string destinationRoot, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<string> ExportSelectionAsync(IReadOnlyList<long> memoryIds, string destinationFolder, bool includeOverlays, IProgress<string>? progress = null, CancellationToken ct = default);
}

public interface IDiscoveryService
{
    Task<ThisDayResult> GetThisDayAsync(DateOnly day, ThisDayOptions options, CancellationToken ct = default);
    Task<SurpriseResult> SurpriseAsync(int count, IReadOnlyCollection<long> excludedIds, CancellationToken ct = default);
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler? Changed;
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

public interface IAnalysisStore
{
    Task SetOcrTextAsync(long memoryId, string text, string engine, CancellationToken ct = default);
    Task<string?> GetOcrTextAsync(long memoryId, CancellationToken ct = default);
    Task ClearOcrAsync(CancellationToken ct = default);

    Task SetVisualHashAsync(long memoryId, ulong hash, CancellationToken ct = default);
    Task<IReadOnlyList<(long MemoryId, ulong Hash)>> GetVisualHashesAsync(CancellationToken ct = default);
    Task ClearVisualAsync(CancellationToken ct = default);

    Task<long> CreatePersonGroupAsync(string? name, CancellationToken ct = default);
    Task<IReadOnlyList<PersonGroupInfo>> GetPersonGroupsAsync(CancellationToken ct = default);
    Task RenamePersonGroupAsync(long groupId, string name, CancellationToken ct = default);
    Task MergePersonGroupsAsync(long targetGroupId, long sourceGroupId, CancellationToken ct = default);
    Task DeletePersonGroupAsync(long groupId, CancellationToken ct = default);
    Task AssignFaceAsync(long faceId, long? groupId, CancellationToken ct = default);
    Task ClearFacesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetMemoriesForPersonAsync(long groupId, CancellationToken ct = default);
}

public sealed record PersonGroupInfo(long Id, string? Name, int FaceCount, int MemoryCount, string? CoverPath);
