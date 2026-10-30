namespace Memo.Core;

public sealed class MemoLibrary : IAsyncDisposable
{
    private readonly MemoCatalog _catalog;
    private readonly SettingsService _settings;
    private bool _disposed;

    private MemoLibrary(
        MemoCatalog catalog,
        ImportService import,
        BackupService backup,
        DiscoveryService discovery,
        SettingsService settings,
        AnalysisStore analysis)
    {
        _catalog = catalog;
        Import = import;
        Backup = backup;
        Discovery = discovery;
        _settings = settings;
        Analysis = analysis;
        _catalog.TimeZoneChanged += OnTimeZoneChanged;
    }

    public LibraryInfo Info => _catalog.Info;
    public MemoCatalog Catalog => _catalog;
    public ImportService Import { get; }
    public BackupService Backup { get; }
    public DiscoveryService Discovery { get; }
    public SettingsService Settings => _settings;
    public AnalysisStore Analysis { get; }

    public static async Task<MemoLibrary> CreateAsync(
        string rootPath,
        string name,
        string timeZoneId,
        CancellationToken ct = default)
    {
        var root = Path.GetFullPath(rootPath);
        var cleanName = name?.Trim();
        if (string.IsNullOrEmpty(cleanName))
        {
            throw new ArgumentException("Le nom de la bibliothèque est obligatoire.", nameof(name));
        }

        var timeZone = TimeZoneHelper.Resolve(timeZoneId);
        if (File.Exists(root))
        {
            throw new InvalidOperationException($"Le chemin « {root} » désigne un fichier, pas un dossier.");
        }

        Directory.CreateDirectory(root);
        var libraryPath = Path.Combine(root, LibraryManifest.FileName);
        var catalogPath = Path.Combine(root, "catalog.db");
        if (File.Exists(libraryPath))
        {
            throw new InvalidOperationException(
                $"Une bibliothèque existe déjà dans « {root} ». Utilisez l'ouverture au lieu de la création.");
        }

        if (File.Exists(catalogPath))
        {
            throw new InvalidOperationException(
                $"Un catalogue existe déjà dans « {root} » sans fichier de bibliothèque cohérent.");
        }

        Directory.CreateDirectory(Path.Combine(root, "media"));
        Directory.CreateDirectory(Path.Combine(root, "derived", "thumbs"));
        Directory.CreateDirectory(Path.Combine(root, "imports"));

        var createdAt = DateTimeOffset.UtcNow;
        var document = new LibraryDocument
        {
            Format = LibraryManifest.FormatName,
            Version = LibraryManifest.CurrentVersion,
            Id = Guid.NewGuid(),
            Name = cleanName,
            TimeZoneId = timeZone.Id,
            CreatedAt = createdAt,
        };

        var info = new LibraryInfo(document.Id, cleanName, root, timeZone.Id, createdAt);
        var catalog = await MemoCatalog.OpenAsync(info, ct).ConfigureAwait(false);
        await LibraryManifest.WriteAsync(root, document, ct).ConfigureAwait(false);
        return await BuildAsync(catalog, ct).ConfigureAwait(false);
    }

    public static async Task<MemoLibrary> OpenAsync(string rootPath, CancellationToken ct = default)
    {
        var info = await LibraryManifest.ReadAsync(rootPath, ct).ConfigureAwait(false);
        var catalogPath = Path.Combine(info.RootPath, "catalog.db");
        if (!File.Exists(catalogPath))
        {
            throw new InvalidOperationException(
                $"Le catalogue de la bibliothèque « {info.RootPath} » est absent (catalog.db).");
        }

        Directory.CreateDirectory(Path.Combine(info.RootPath, "media"));
        Directory.CreateDirectory(Path.Combine(info.RootPath, "derived", "thumbs"));
        Directory.CreateDirectory(Path.Combine(info.RootPath, "imports"));

        var catalog = await MemoCatalog.OpenAsync(info, ct).ConfigureAwait(false);
        return await BuildAsync(catalog, ct).ConfigureAwait(false);
    }

    private static async Task<MemoLibrary> BuildAsync(MemoCatalog catalog, CancellationToken ct)
    {
        var settings = await SettingsService.LoadAsync(catalog, catalog.TimeZoneId, ct).ConfigureAwait(false);
        return new MemoLibrary(
            catalog,
            new ImportService(catalog),
            new BackupService(catalog),
            new DiscoveryService(catalog),
            settings,
            new AnalysisStore(catalog));
    }

    private void OnTimeZoneChanged(object? sender, string timeZoneId) =>
        _settings.ApplyTimeZoneChange(timeZoneId);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _catalog.TimeZoneChanged -= OnTimeZoneChanged;
        return ValueTask.CompletedTask;
    }
}
