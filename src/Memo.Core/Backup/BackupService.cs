using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Memo.Core;

/// <summary>Étape d'une sauvegarde ou d'une restauration.</summary>
public enum BackupOperationPhase
{
    Preparing,
    Catalog,
    Media,
    Extracting,
    Verifying,
    Finalizing,
    Completed
}

/// <summary>
/// Progression d'une sauvegarde ou d'une restauration. La fraction est calculée
/// sur les octets quand ils sont connus, sinon sur le nombre de fichiers.
/// </summary>
public sealed record BackupProgress(
    BackupOperationPhase Phase,
    string Message,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    string? CurrentItem = null)
{
    public double Fraction => BytesTotal > 0
        ? Math.Clamp((double)BytesDone / BytesTotal, 0d, 1d)
        : FilesTotal > 0
            ? Math.Clamp((double)FilesDone / FilesTotal, 0d, 1d)
            : 0d;
}

public sealed class BackupService : IBackupService
{
    private readonly MemoCatalog _catalog;

    internal BackupService(MemoCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Version historique conservant la progression textuelle.</summary>
    public Task<BackupResult> CreateBackupAsync(
        string destinationZip,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => CreateBackupWithProgressAsync(destinationZip, WrapLegacy(progress), ct);

    /// <summary>
    /// Sauvegarde atomique : l'archive est écrite dans un fichier temporaire du
    /// même dossier puis renommée seulement une fois complète et fermée. Une
    /// annulation ou un échec ne laisse donc jamais d'archive partielle à la
    /// place de la sauvegarde demandée.
    /// </summary>
    public async Task<BackupResult> CreateBackupWithProgressAsync(
        string destinationZip,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var destination = Path.GetFullPath(destinationZip);
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent))
        {
            throw new InvalidOperationException("Le dossier de destination de la sauvegarde est invalide.");
        }
        Directory.CreateDirectory(parent);

        var fileName = Path.GetFileName(destination);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Le nom de la sauvegarde est invalide.");
        }

        var libraryPath = Path.Combine(_catalog.RootPath, LibraryManifest.FileName);
        if (!File.Exists(libraryPath))
        {
            throw new InvalidOperationException("Le fichier library.json de la bibliothèque est absent.");
        }

        var temporaryDatabase = Path.Combine(
            Path.GetTempPath(),
            $"memo-backup-{Guid.NewGuid():N}.db");
        var temporaryArchive = Path.Combine(
            parent,
            $".{fileName}.backup-{Guid.NewGuid():N}.part");
        var published = false;

        try
        {
            var tally = new ProgressTally
            {
                Phase = BackupOperationPhase.Preparing,
                Message = "Préparation d'une copie cohérente du catalogue…"
            };
            progress?.Report(tally.Snapshot());

            await _catalog.VacuumIntoAsync(temporaryDatabase, ct).ConfigureAwait(false);

            var memoryCount = await _catalog.CountAsync(new MemoryQuery(), ct).ConfigureAwait(false);
            var sourceStats = await _catalog.GetSourceStatsAsync(ct).ConfigureAwait(false);
            var assets = await _catalog.GetStoredAssetFilesAsync(ct).ConfigureAwait(false);
            var totalBytes = assets.Sum(asset => asset.Size);
            var databaseBytes = new FileInfo(temporaryDatabase).Length;
            var manifestBytes = new FileInfo(libraryPath).Length;

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

            tally.Phase = BackupOperationPhase.Catalog;
            tally.Message = "Copie du catalogue et du manifeste de bibliothèque";
            tally.FilesTotal = assets.Count + 3;
            tally.BytesTotal = totalBytes + databaseBytes + manifestBytes;
            progress?.Report(tally.Snapshot());

            await using (var fileStream = new FileStream(
                             temporaryArchive,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true);

                await AddFileAsync(
                        archive, "catalog.db", temporaryDatabase, tally, progress, ct)
                    .ConfigureAwait(false);
                await AddFileAsync(
                        archive, LibraryManifest.FileName, libraryPath, tally, progress, ct)
                    .ConfigureAwait(false);

                tally.Phase = BackupOperationPhase.Media;
                tally.Message = "Copie des médias sources";
                foreach (var asset in assets)
                {
                    ct.ThrowIfCancellationRequested();
                    var fullPath = ImportPaths.ToPlatformPath(_catalog.RootPath, asset.RelativePath);
                    if (!File.Exists(fullPath))
                    {
                        throw new InvalidOperationException(
                            $"Le fichier média « {asset.RelativePath} » est absent ; sauvegarde interrompue.");
                    }

                    tally.CurrentItem = asset.RelativePath;
                    await AddFileAsync(
                            archive, asset.RelativePath, fullPath, tally, progress, ct)
                        .ConfigureAwait(false);
                }

                tally.Phase = BackupOperationPhase.Finalizing;
                tally.Message = "Écriture du manifeste…";
                tally.CurrentItem = "manifest.json";
                progress?.Report(tally.Snapshot());
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonSupport.Options, ct)
                        .ConfigureAwait(false);
                }
                tally.FilesDone++;
                tally.CurrentItem = "manifest.json";
            }

            // Dernière étape : publier l'archive complète sous son nom définitif.
            // Un dernier contrôle évite de publier une archive si l'annulation
            // est arrivée pendant l'écriture du manifeste.
            ct.ThrowIfCancellationRequested();
            tally.Phase = BackupOperationPhase.Finalizing;
            tally.Message = "Finalisation de la sauvegarde…";
            progress?.Report(tally.Snapshot());
            File.Move(temporaryArchive, destination, overwrite: true);
            published = true;

            tally.Phase = BackupOperationPhase.Completed;
            tally.Message = $"Sauvegarde terminée : {destination}";
            tally.CurrentItem = null;
            progress?.Report(tally.Snapshot());
            return new BackupResult(destination, manifest);
        }
        finally
        {
            if (!published)
            {
                TryDeleteFile(temporaryArchive);
            }
            TryDeleteFile(temporaryDatabase);
        }
    }

    /// <summary>Version historique conservant la progression textuelle.</summary>
    public Task RestoreAsync(
        string backupZip,
        string destinationRoot,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => RestoreWithProgressAsync(backupZip, destinationRoot, WrapLegacy(progress), ct);

    /// <summary>
    /// Restaure dans un dossier vide. L'extraction a lieu dans un dossier
    /// temporaire frère ; le déplacement final n'a lieu qu'après vérification
    /// du catalogue et du SHA-256 de chaque média. Une annulation supprime le
    /// temporaire sans toucher à la destination.
    /// </summary>
    public async Task RestoreWithProgressAsync(
        string backupZip,
        string destinationRoot,
        IProgress<BackupProgress>? progress = null,
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
                long totalBytes = 0;
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
                    totalBytes += entry.Length;
                }

                foreach (var required in new[] { "catalog.db", LibraryManifest.FileName, "manifest.json" })
                {
                    if (!names.Contains(required))
                    {
                        throw new InvalidDataException(
                            $"Sauvegarde incomplète : « {required} » est absent.");
                    }
                }

                var tally = new ProgressTally
                {
                    Phase = BackupOperationPhase.Extracting,
                    Message = "Extraction de la sauvegarde",
                    FilesTotal = entries.Count,
                    BytesTotal = totalBytes
                };
                progress?.Report(tally.Snapshot());

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
                    await PumpAsync(input, output, tally, progress, ct).ConfigureAwait(false);

                    tally.FilesDone++;
                    tally.CurrentItem = normalized;
                    progress?.Report(tally.Snapshot());
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

            progress?.Report(new BackupProgress(
                BackupOperationPhase.Verifying,
                "Vérification du catalogue restauré…",
                0,
                0,
                0,
                0));

            int restoredCount;
            Guid restoredId;
            IReadOnlyList<CatalogAsset> assets;
            await using (var library = await MemoLibrary.OpenAsync(temporary, ct).ConfigureAwait(false))
            {
                restoredCount = await library.Catalog.CountAsync(new MemoryQuery(), ct).ConfigureAwait(false);
                restoredId = library.Info.Id;
                // L'API publique du catalogue n'expose que chemin et taille. La
                // lecture seule de la table assets donne le SHA-256 de référence
                // sans modifier le catalogue restauré.
                assets = await ReadCatalogAssetsAsync(
                        Path.Combine(temporary, "catalog.db"), ct)
                    .ConfigureAwait(false);
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

            await VerifyAssetsAsync(temporary, assets, progress, ct).ConfigureAwait(false);

            // Dernier point d'annulation : après ce contrôle, le déplacement est
            // atomique et la restauration est considérée comme terminée.
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: false);
            }

            Directory.Move(temporary, destination);
            moved = true;
            progress?.Report(new BackupProgress(
                BackupOperationPhase.Completed,
                $"Restauration terminée : {destination}",
                assets.Count,
                assets.Count,
                assets.Sum(asset => asset.Size),
                assets.Sum(asset => asset.Size)));
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

    /// <summary>
    /// Vérifie que chaque asset du catalogue existe, a la taille annoncée et
    /// le SHA-256 attendu. Les caches dérivés (miniatures, composites) sont
    /// volontairement absents d'une sauvegarde : ils ne sont pas vérifiés et
    /// seront reconstruits après l'ouverture.
    /// </summary>
    private static async Task VerifyAssetsAsync(
        string root,
        IReadOnlyList<CatalogAsset> assets,
        IProgress<BackupProgress>? progress,
        CancellationToken ct)
    {
        var tally = new ProgressTally
        {
            Phase = BackupOperationPhase.Verifying,
            Message = "Vérification des médias (taille et SHA-256)",
            FilesTotal = assets.Count,
            BytesTotal = assets.Sum(asset => asset.Size)
        };
        progress?.Report(tally.Snapshot());

        var problems = new List<MediaProblem>();
        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();
            tally.CurrentItem = asset.StoredPath;
            var fullPath = ImportPaths.ToPlatformPath(root, asset.StoredPath);
            if (!ImportPaths.IsInsideRoot(root, fullPath))
            {
                problems.Add(new MediaProblem(asset.StoredPath, "chemin hors de la bibliothèque"));
            }
            else if (!File.Exists(fullPath))
            {
                problems.Add(new MediaProblem(asset.StoredPath, "fichier absent"));
            }
            else
            {
                var length = new FileInfo(fullPath).Length;
                if (length != asset.Size)
                {
                    problems.Add(new MediaProblem(
                        asset.StoredPath,
                        $"taille {length} octets au lieu de {asset.Size}"));
                }
                else
                {
                    await using var stream = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var hash = Convert.ToHexString(
                            await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false))
                        .ToLowerInvariant();
                    if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add(new MediaProblem(asset.StoredPath, "SHA-256 différent"));
                    }
                }
            }

            tally.FilesDone++;
            tally.BytesDone += asset.Size;
            progress?.Report(tally.Snapshot());
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(BuildVerificationMessage(problems, assets.Count));
        }
    }

    private static string BuildVerificationMessage(IReadOnlyList<MediaProblem> problems, int total)
    {
        var details = string.Join(
            " · ",
            problems.Take(10).Select(problem => $"{problem.RelativePath} ({problem.Reason})"));
        var suffix = problems.Count > 10
            ? $" · et {problems.Count - 10} autre(s)"
            : string.Empty;
        return $"Sauvegarde corrompue : {problems.Count} média(s) sur {total} sont manquants, " +
               $"tronqués ou modifiés. Aucun fichier n'a été déplacé vers le dossier final. " +
               $"{details}{suffix}";
    }

    /// <summary>
    /// Lit les assets du catalogue restauré. La connexion est ouverte en lecture
    /// seule et sans pool pour ne pas verrouiller le déplacement final.
    /// </summary>
    private static async Task<IReadOnlyList<CatalogAsset>> ReadCatalogAssetsAsync(
        string catalogPath,
        CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = catalogPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stored_path, size, sha256 FROM assets ORDER BY stored_path;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<CatalogAsset>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new CatalogAsset(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        }

        return result;
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        string entryName,
        string sourcePath,
        ProgressTally tally,
        IProgress<BackupProgress>? progress,
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
        await PumpAsync(input, output, tally, progress, ct).ConfigureAwait(false);
        tally.FilesDone++;
        tally.CurrentItem = entryName;
        progress?.Report(tally.Snapshot());
    }

    /// <summary>
    /// Copie par blocs en mettant à jour les octets lus. Un rapport intermédiaire
    /// toutes les 8 Mio évite de saturer la file UI sur les très gros médias.
    /// </summary>
    private static async Task PumpAsync(
        Stream input,
        Stream output,
        ProgressTally tally,
        IProgress<BackupProgress>? progress,
        CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];
        long sinceReport = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            tally.BytesDone += read;
            sinceReport += read;
            if (sinceReport >= 8L * 1024 * 1024)
            {
                sinceReport = 0;
                progress?.Report(tally.Snapshot());
            }
        }
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

    /// <summary>Adapte la progression typée à l'ancienne surcharge textuelle.</summary>
    private static IProgress<BackupProgress>? WrapLegacy(IProgress<string>? progress)
        => progress is null ? null : new LegacyProgress(progress);

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

    private sealed class LegacyProgress(IProgress<string> inner) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => inner.Report(value.CurrentItem ?? value.Message);
    }

    private sealed class ProgressTally
    {
        public BackupOperationPhase Phase { get; set; }
        public string Message { get; set; } = string.Empty;
        public int FilesDone { get; set; }
        public int FilesTotal { get; set; }
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }
        public string? CurrentItem { get; set; }

        public BackupProgress Snapshot()
            => new(Phase, Message, FilesDone, FilesTotal, BytesDone, BytesTotal, CurrentItem);
    }

    private sealed record CatalogAsset(string StoredPath, long Size, string Sha256);

    private sealed record MediaProblem(string RelativePath, string Reason);
}
