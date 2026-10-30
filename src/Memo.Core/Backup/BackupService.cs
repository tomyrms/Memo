using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace Memo.Core;

public sealed class BackupService : IBackupService
{
    private readonly MemoCatalog _catalog;

    internal BackupService(MemoCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task<BackupResult> CreateBackupAsync(
        string destinationZip,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var destination = Path.GetFullPath(destinationZip);
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var libraryPath = Path.Combine(_catalog.RootPath, LibraryManifest.FileName);
        if (!File.Exists(libraryPath))
        {
            throw new InvalidOperationException("Le fichier library.json de la bibliothèque est absent.");
        }

        var temporaryDatabase = Path.Combine(
            Path.GetTempPath(),
            $"memo-backup-{Guid.NewGuid():N}.db");

        try
        {
            progress?.Report("Préparation d'une copie cohérente du catalogue…");
            await _catalog.VacuumIntoAsync(temporaryDatabase, ct).ConfigureAwait(false);

            var memoryCount = await _catalog.CountAsync(new MemoryQuery(), ct).ConfigureAwait(false);
            var sourceStats = await _catalog.GetSourceStatsAsync(ct).ConfigureAwait(false);
            var assets = await _catalog.GetStoredAssetFilesAsync(ct).ConfigureAwait(false);
            var totalBytes = assets.Sum(asset => asset.Size);

            var manifest = new BackupManifest(
                1,
                _catalog.Info.Id,
                _catalog.Info.Name,
                DateTimeOffset.UtcNow,
                memoryCount,
                sourceStats.Count,
                totalBytes,
                new[] { "catalog.db", "library.json", "media/", "settings" },
                new[] { "derived/", "imports/" });

            await using var fileStream = new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

            await AddFileAsync(archive, "catalog.db", temporaryDatabase, progress, ct).ConfigureAwait(false);
            await AddFileAsync(archive, LibraryManifest.FileName, libraryPath, progress, ct).ConfigureAwait(false);

            foreach (var asset in assets)
            {
                ct.ThrowIfCancellationRequested();
                var fullPath = ImportPaths.ToPlatformPath(_catalog.RootPath, asset.RelativePath);
                if (!File.Exists(fullPath))
                {
                    throw new InvalidOperationException(
                        $"Le fichier média « {asset.RelativePath} » est absent ; sauvegarde interrompue.");
                }

                await AddFileAsync(archive, asset.RelativePath, fullPath, progress, ct).ConfigureAwait(false);
            }

            progress?.Report("Écriture du manifeste…");
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using (var manifestStream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonSupport.Options, ct)
                    .ConfigureAwait(false);
            }

            progress?.Report($"Sauvegarde terminée : {destination}");
            return new BackupResult(destination, manifest);
        }
        finally
        {
            TryDeleteFile(temporaryDatabase);
        }
    }

    public async Task RestoreAsync(
        string backupZip,
        string destinationRoot,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var backup = Path.GetFullPath(backupZip);
        if (!File.Exists(backup))
        {
            throw new FileNotFoundException("La sauvegarde demandée est introuvable.", backup);
        }

        var destination = Path.GetFullPath(destinationRoot);
        if (File.Exists(destination))
        {
            throw new InvalidOperationException($"« {destination} » est un fichier, pas un dossier de restauration.");
        }

        if (Directory.Exists(destination) &&
            Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new InvalidOperationException(
                $"Le dossier de restauration « {destination} » n'est pas vide ; aucun fichier n'a été écrasé.");
        }

        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidOperationException("Le dossier de destination de la restauration est invalide.");
        }

        Directory.CreateDirectory(parent);
        var name = Path.GetFileName(destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Le dossier de destination de la restauration est invalide.");
        }

        var temporary = Path.Combine(parent, $".{name}.restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);

        var moved = false;
        try
        {
            ZipArchive archive;
            try
            {
                archive = ZipFile.OpenRead(backup);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"La sauvegarde « {backup} » est illisible : {ex.Message}", ex);
            }

            using (archive)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var entries = new List<(ZipArchiveEntry Entry, string Normalized)>();
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        continue;
                    }

                    if (!ImportPaths.TryNormalize(entry.FullName, out var normalized, out var error))
                    {
                        throw new InvalidDataException(
                            $"Entrée de sauvegarde refusée ({error}) : {entry.FullName}");
                    }

                    if (!names.Add(normalized))
                    {
                        throw new InvalidDataException($"Entrée de sauvegarde dupliquée : {normalized}");
                    }

                    entries.Add((entry, normalized));
                }

                foreach (var required in new[] { "catalog.db", LibraryManifest.FileName, "manifest.json" })
                {
                    if (!names.Contains(required))
                    {
                        throw new InvalidDataException(
                            $"Sauvegarde incomplète : « {required} » est absent.");
                    }
                }

                foreach (var (entry, normalized) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = ImportPaths.ToPlatformPath(temporary, normalized);
                    if (!ImportPaths.IsInsideRoot(temporary, target))
                    {
                        throw new InvalidDataException($"Chemin de sauvegarde hors du dossier cible : {normalized}");
                    }

                    var directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    await using var input = entry.Open();
                    await using var output = new FileStream(
                        target,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, 128 * 1024, ct).ConfigureAwait(false);
                    progress?.Report(normalized);
                }
            }

            BackupManifest? manifest;
            await using (var manifestStream = new FileStream(
                             Path.Combine(temporary, "manifest.json"),
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
                        manifestStream, JsonSupport.Options, ct)
                    .ConfigureAwait(false);
            }

            if (manifest is null || manifest.Version <= 0 || manifest.Version > 1)
            {
                throw new InvalidDataException("Le manifeste de sauvegarde est invalide ou trop récent.");
            }

            progress?.Report("Vérification du catalogue restauré…");
            int restoredCount;
            Guid restoredId;
            await using (var library = await MemoLibrary.OpenAsync(temporary, ct).ConfigureAwait(false))
            {
                restoredCount = await library.Catalog.CountAsync(new MemoryQuery(), ct).ConfigureAwait(false);
                restoredId = library.Info.Id;
            }

            if (restoredId != manifest.LibraryId)
            {
                throw new InvalidDataException(
                    "Le catalogue restauré n'appartient pas à la bibliothèque annoncée par le manifeste.");
            }

            if (restoredCount != manifest.MemoryCount)
            {
                throw new InvalidDataException(
                    $"Le catalogue restauré contient {restoredCount} souvenir(s), " +
                    $"alors que le manifeste en annonce {manifest.MemoryCount}.");
            }

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: false);
            }

            Directory.Move(temporary, destination);
            moved = true;
            progress?.Report($"Restauration terminée : {destination}");
        }
        finally
        {
            if (!moved)
            {
                TryDeleteDirectory(temporary);
            }
        }
    }

    public async Task<string> ExportSelectionAsync(
        IReadOnlyList<long> memoryIds,
        string destinationFolder,
        bool includeOverlays,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var destination = Path.GetFullPath(destinationFolder);
        if (File.Exists(destination))
        {
            throw new InvalidOperationException($"« {destination} » est un fichier, pas un dossier d'export.");
        }

        Directory.CreateDirectory(destination);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exportedItems = new List<object>();

        foreach (var memoryId in (memoryIds ?? Array.Empty<long>()).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var memory = await _catalog.GetExportMemoryAsync(memoryId, ct).ConfigureAwait(false);
            if (memory is null)
            {
                progress?.Report($"Souvenir {memoryId} introuvable, ignoré.");
                continue;
            }

            var exportedFiles = new List<object>();
            foreach (var file in memory.Files)
            {
                if (file.Role == MediaRole.Overlay && !includeOverlays)
                {
                    continue;
                }

                var extension = MemoCatalog.CanonicalExtension(file.Format);
                var fileName = MemoCatalog.FormatExportFileName(
                    memory.LocalDate,
                    memory.LocalTime,
                    memory.ExternalKey,
                    extension,
                    file.Role == MediaRole.Overlay);
                if (!usedNames.Add(fileName))
                {
                    throw new InvalidOperationException(
                        $"Deux souvenirs produisent le même nom d'export « {fileName} ».");
                }

                var source = ImportPaths.ToPlatformPath(_catalog.RootPath, file.StoredPath);
                if (!File.Exists(source))
                {
                    throw new FileNotFoundException(
                        $"Le fichier source du souvenir {memoryId} est absent.", source);
                }

                var target = Path.Combine(destination, fileName);
                await CopyFileAsync(source, target, ct).ConfigureAwait(false);
                progress?.Report(fileName);
                exportedFiles.Add(new
                {
                    role = file.Role == MediaRole.Overlay ? "overlay" : "main",
                    fileName,
                    file.Sha256,
                    file.Size,
                    file.Format,
                    provenance = new
                    {
                        archivePath = file.ArchivePath,
                        entryPath = file.EntryPath,
                    },
                });
            }

            exportedItems.Add(new
            {
                memoryId = memory.MemoryId,
                externalKey = memory.ExternalKey,
                status = memory.Status.ToString(),
                localDate = DateText.Date(memory.LocalDate),
                localTime = DateText.Time(memory.LocalTime),
                notes = memory.Note,
                files = exportedFiles,
                dateCandidates = memory.Candidates.Select(candidate => new
                {
                    raw = candidate.RawValue,
                    source = candidate.Source.ToString(),
                    meaning = candidate.Meaning.ToString(),
                    precision = candidate.Precision.ToString(),
                    timezone = candidate.TimeZoneId,
                    instantUtc = candidate.InstantUtc,
                    localDate = DateText.Date(candidate.LocalDate),
                    candidate.Confidence,
                }),
                dateOverride = memory.Override is null
                    ? null
                    : new
                    {
                        localDate = DateText.Date(memory.Override.LocalDate),
                        precision = memory.Override.Precision.ToString(),
                        note = memory.Override.Note,
                        updatedAt = memory.Override.UpdatedAt,
                    },
            });
        }

        var metadata = new
        {
            libraryId = _catalog.Info.Id,
            libraryName = _catalog.Info.Name,
            exportedAt = DateTimeOffset.UtcNow,
            timeZoneId = _catalog.TimeZoneId,
            items = exportedItems,
        };
        var metadataPath = Path.Combine(destination, "metadata.json");
        await using (var stream = new FileStream(
                         metadataPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, metadata, JsonSupport.Options, ct).ConfigureAwait(false);
        }

        progress?.Report($"Export terminé : {destination}");
        return destination;
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        string entryName,
        string sourcePath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = entry.Open();
        await input.CopyToAsync(output, 128 * 1024, ct).ConfigureAwait(false);
        progress?.Report(entryName);
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 128 * 1024, ct).ConfigureAwait(false);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
