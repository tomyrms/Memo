using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Memo.Core;

public sealed partial class ImportService : IImportService
{
    private static readonly Regex MemoryEntryPattern = new(
        "^memories/(?<uuid>[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})-(?<role>main|overlay)\\.(?<ext>jpg|jpeg|mp4|png|webp)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "mp4", "png", "webp" };

    private const int MaxArchiveEntries = 100_000;
    private const long MaxArchiveUncompressedBytes = 40L * 1024 * 1024 * 1024;
    private const long MaxEntryUncompressedBytes = 4L * 1024 * 1024 * 1024;
    private const double MaxCompressionRatio = 1000.0;

    private static bool ValidateArchiveLimits(ZipArchive archive, string path, ImportContext context)
    {
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            context.AddError(
                "archive.too-many-entries",
                $"Archive refusée : {archive.Entries.Count} entrées dépassent la limite de {MaxArchiveEntries}.",
                path);
            return false;
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > MaxEntryUncompressedBytes)
            {
                context.AddError(
                    "archive.entry-too-large",
                    $"Entrée refusée : {entry.FullName} dépasse la limite de {MaxEntryUncompressedBytes / (1024 * 1024 * 1024)} Gio.",
                    path,
                    entry.FullName);
                return false;
            }

            if (entry.CompressedLength > 0 &&
                entry.Length > 1024 * 1024 &&
                entry.Length / (double)entry.CompressedLength > MaxCompressionRatio)
            {
                context.AddError(
                    "archive.suspicious-ratio",
                    $"Archive refusée : ratio de compression anormal pour {entry.FullName}.",
                    path,
                    entry.FullName);
                return false;
            }

            total += entry.Length;
            if (total > MaxArchiveUncompressedBytes)
            {
                context.AddError(
                    "archive.too-large",
                    $"Archive refusée : taille décompressée supérieure à {MaxArchiveUncompressedBytes / (1024 * 1024 * 1024)} Gio.",
                    path);
                return false;
            }
        }

        return true;
    }

    private readonly MemoCatalog _catalog;

    internal ImportService(MemoCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task<ImportResult> RunAsync(
        ImportRequest request,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = DateTimeOffset.UtcNow;
        var state = new CounterState();
        var diagnostics = new List<ImportDiagnostic>();
        var reporter = new ProgressReporter(progress, state);
        var context = new ImportContext(_catalog, request, state, diagnostics, reporter);

        var batchId = await _catalog.BeginBatchAsync(request.SpaceId, request.BatchNote, ct)
            .ConfigureAwait(false);
        context.BatchId = batchId;
        var status = "running";
        var cancelled = false;

        try
        {
            reporter.Report(ImportPhase.Scanning, "Inventaire des archives…", force: true);
            var inputList = ExpandInputs(request.Paths, context);
            state.ArchivesSeen = inputList.Count;
            if (inputList.Count == 0)
            {
                context.AddWarning(
                    "import.empty",
                    "Aucune archive ou export exploitable n'a été fourni.");
            }

            var total = 0;
            foreach (var input in inputList)
            {
                total += await CountEntriesAsync(input, ct).ConfigureAwait(false);
            }

            reporter.FilesTotal = total;
            reporter.Report(
                ImportPhase.Reading,
                $"{inputList.Count} archive(s), {total} entrée(s) à examiner…",
                force: true);

            foreach (var input in inputList)
            {
                ct.ThrowIfCancellationRequested();
                reporter.Report(
                    ImportPhase.Reading,
                    input.IsZip ? "Lecture de l'archive…" : "Lecture de l'export extrait…",
                    input.Path,
                    force: true);

                try
                {
                    if (input.IsZip)
                    {
                        await ImportZipAsync(input, context, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await ImportFolderAsync(input, context, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsRecoverableIo(ex))
                {
                    context.SetFatal();
                    context.AddError(
                        "archive.io-error",
                        $"Lecture impossible : {ex.Message}",
                        input.Path);
                }
            }

            status = context.Fatal ? "failed" : "completed";
            reporter.Report(
                ImportPhase.Done,
                status == "failed" ? "Import terminé avec des erreurs." : "Import terminé.",
                force: true);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            status = "cancelled";
            reporter.Report(ImportPhase.Cancelled, "Import annulé.", force: true);
        }
        catch (Exception ex) when (IsRecoverableIo(ex))
        {
            context.SetFatal();
            context.AddError("import.io-error", $"Import interrompu : {ex.Message}");
            status = "failed";
            reporter.Report(ImportPhase.Failed, "Import interrompu.", force: true);
        }
        finally
        {
            try
            {
                await _catalog.FinishBatchAsync(batchId, status, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableIo(ex))
            {
                // Le lot ne peut pas être clôturé (disque plein, verrou) : le diagnostic reste disponible.
                diagnostics.Add(new ImportDiagnostic(
                    "error",
                    "batch.finalize-error",
                    $"Impossible d'enregistrer l'état final du lot : {ex.Message}"));
            }
        }

        return new ImportResult(
            batchId,
            state.Snapshot(),
            diagnostics,
            startedAt,
            DateTimeOffset.UtcNow,
            cancelled);
    }

    public async Task<int> InspectAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var path in paths ?? Array.Empty<string>())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                var zips = Directory
                    .EnumerateFiles(path, "*.zip", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (!HasSubdirectory(path, "memories") && !HasSubdirectory(path, "chat_media") && zips.Count > 0)
                {
                    foreach (var zip in zips)
                    {
                        total += await CountMediaInZipAsync(zip, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    total += CountMediaInFolder(path);
                }
            }
            else if (File.Exists(path) &&
                     string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                total += await CountMediaInZipAsync(path, ct).ConfigureAwait(false);
            }
        }

        return total;
    }

    private static async Task<int> CountMediaInZipAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var count = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                if (ImportPaths.TryNormalize(entry.FullName, out var normalized, out _) &&
                    MemoryEntryPattern.IsMatch(normalized))
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception ex) when (IsRecoverableIo(ex))
        {
            return 0;
        }
    }

    private static int CountMediaInFolder(string root)
    {
        try
        {
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (ImportPaths.TryNormalize(relative, out var normalized, out _) &&
                    MemoryEntryPattern.IsMatch(normalized))
                {
                    count++;
                }
            }

            return count;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<InputSource> ExpandInputs(
        IReadOnlyList<string> paths,
        ImportContext context)
    {
        var inputs = new List<InputSource>();
        foreach (var rawPath in paths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(rawPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                context.SetFatal();
                context.AddError("path.invalid", $"Chemin invalide : {ex.Message}", rawPath);
                continue;
            }

            if (File.Exists(full))
            {
                if (string.Equals(Path.GetExtension(full), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    inputs.Add(new InputSource(full, true));
                }
                else
                {
                    context.SetFatal();
                    context.AddError(
                        "path.unsupported",
                        "Le fichier sélectionné n'est pas une archive ZIP.",
                        full);
                }
            }
            else if (Directory.Exists(full))
            {
                var hasExport = HasSubdirectory(full, "memories") || HasSubdirectory(full, "chat_media");
                var zips = Directory
                    .EnumerateFiles(full, "*.zip", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!hasExport && zips.Count > 0)
                {
                    foreach (var zip in zips)
                    {
                        inputs.Add(new InputSource(zip, true));
                    }
                }
                else
                {
                    inputs.Add(new InputSource(full, false));
                }
            }
            else
            {
                context.SetFatal();
                context.AddError(
                    "path.missing",
                    "Le chemin sélectionné est introuvable.",
                    full);
            }
        }

        return inputs;
    }

    private static bool HasSubdirectory(string root, string name)
    {
        try
        {
            return Directory
                .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Any(directory => string.Equals(Path.GetFileName(directory), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<int> CountEntriesAsync(InputSource input, CancellationToken ct)
    {
        try
        {
            if (input.IsZip)
            {
                await using var stream = new FileStream(
                    input.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                ct.ThrowIfCancellationRequested();
                return archive.Entries.Count;
            }

            return Directory.EnumerateFiles(input.Path, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex) when (IsRecoverableIo(ex))
        {
            return 0;
        }
    }

    private async Task ImportZipAsync(InputSource input, ImportContext context, CancellationToken ct)
    {
        context.CurrentArchivePath = input.Path;
        string sha;
        long size;
        try
        {
            var info = new FileInfo(input.Path);
            size = info.Length;
            await using var stream = new FileStream(
                input.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            sha = Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (IsRecoverableIo(ex))
        {
            context.SetFatal();
            context.AddError("archive.read-error", $"Archive illisible : {ex.Message}", input.Path);
            return;
        }

        var existing = await _catalog.FindArchiveAsync(context.SpaceId, sha, ct).ConfigureAwait(false);
        if (!context.IncludeChatMedia &&
            existing is not null &&
            string.Equals(existing.Status, "complete", StringComparison.Ordinal))
        {
            context.State.DuplicatesSkipped++;
            context.Reporter.FilesProcessed += await CountEntriesAsync(input, ct).ConfigureAwait(false);
            context.AddInfo(
                "archive.duplicate",
                "Archive déjà importée ; aucune nouvelle entrée n'a été créée.",
                input.Path);
            return;
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(input.Path);
        }
        catch (Exception ex) when (IsRecoverableIo(ex))
        {
            context.SetFatal();
            context.AddError("archive.corrupt", $"Archive ZIP corrompue ou illisible : {ex.Message}", input.Path);
            return;
        }

        using (archive)
        {
            if (!ValidateArchiveLimits(archive, input.Path, context))
            {
                return;
            }

            var entryCount = archive.Entries.Count;
            var registration = await _catalog.RegisterArchiveAsync(
                context.BatchId,
                context.SpaceId,
                input.Path,
                sha,
                size,
                "processing",
                entryCount,
                ct).ConfigureAwait(false);

            if (!registration.Created && !context.IncludeChatMedia)
            {
                var current = await _catalog.FindArchiveAsync(context.SpaceId, sha, ct).ConfigureAwait(false);
                if (current is not null &&
                    string.Equals(current.Status, "complete", StringComparison.Ordinal))
                {
                    context.State.DuplicatesSkipped++;
                    context.Reporter.FilesProcessed += entryCount;
                    context.AddInfo("archive.duplicate", "Archive déjà importée.", input.Path);
                    return;
                }
            }
            else
            {
                context.State.ArchivesImported++;
            }

            var archiveFatal = false;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                context.State.EntriesSeen++;
                var entryPath = entry.FullName;
                try
                {
                    await ProcessZipEntryAsync(entry, registration.Id, context, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsRecoverableIo(ex))
                {
                    archiveFatal = true;
                    context.SetFatal();
                    context.AddError(
                        "entry.io-error",
                        $"Entrée illisible : {ex.Message}",
                        input.Path,
                        entryPath);
                }
                finally
                {
                    context.Advance(entryPath, ImportPhase.Storing);
                }
            }

            if (archiveFatal)
            {
                await _catalog.SetArchiveStatusAsync(registration.Id, "failed", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await _catalog.SetArchiveStatusAsync(registration.Id, "complete", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessZipEntryAsync(
        ZipArchiveEntry entry,
        long archiveId,
        ImportContext context,
        CancellationToken ct)
    {
        var entryPath = entry.FullName;
        if (entryPath.EndsWith('/') || entryPath.EndsWith('\\'))
        {
            return;
        }

        if (!ImportPaths.TryNormalize(entryPath, out var normalized, out var pathError))
        {
            context.AddError(
                "entry.invalid-path",
                $"Entrée refusée ({pathError}) : {entryPath}",
                context.CurrentArchivePath,
                entryPath);
            return;
        }

        var extension = ImportPaths.Extension(normalized);
        if (string.Equals(extension, "zip", StringComparison.OrdinalIgnoreCase))
        {
            context.State.Unrecognized++;
            context.AddWarning(
                "archive.nested",
                "Archive imbriquée ignorée : aucun adaptateur ne la reconnaît.",
                context.CurrentArchivePath,
                entryPath);
            return;
        }

        if (normalized.StartsWith("chat_media/", StringComparison.OrdinalIgnoreCase))
        {
            if (!context.IncludeChatMedia || !SupportedExtensions.Contains(extension))
            {
                return;
            }

            await using var chatStream = entry.Open();
            await ProcessMediaAsync(
                chatStream,
                normalized,
                extension,
                MemoCatalog.RoleChat,
                null,
                archiveId,
                context,
                ct).ConfigureAwait(false);
            return;
        }

        if (!normalized.StartsWith("memories/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var match = MemoryEntryPattern.Match(normalized);
        if (!match.Success)
        {
            context.State.Unrecognized++;
            if (SupportedExtensions.Contains(extension))
            {
                context.AddWarning(
                    "entry.unrecognized",
                    "Fichier non reconnu dans memories/ ; il est ignoré.",
                    context.CurrentArchivePath,
                    entryPath);
            }

            return;
        }

        var roleText = match.Groups["role"].Value.ToLowerInvariant();
        if (roleText == "overlay" && string.Equals(extension, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            context.State.Unrecognized++;
            context.AddWarning(
                "entry.role-format",
                "Un calque vidéo n'est pas reconnu.",
                context.CurrentArchivePath,
                entryPath);
            return;
        }

        var role = roleText == "overlay" ? MediaRole.Overlay : MediaRole.Main;
        await using var stream = entry.Open();
        await ProcessMediaAsync(
            stream,
            normalized,
            extension,
            roleText,
            role,
            archiveId,
            context,
            ct).ConfigureAwait(false);
    }

    private async Task ImportFolderAsync(InputSource input, ImportContext context, CancellationToken ct)
    {
        context.CurrentArchivePath = input.Path;
        var files = new List<FolderEntry>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(input.Path, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(input.Path, file).Replace('\\', '/');
                if (!ImportPaths.TryNormalize(relative, out var normalized, out var error))
                {
                    context.AddError(
                        "entry.invalid-path",
                        $"Chemin de fichier refusé ({error}) : {relative}",
                        input.Path,
                        relative);
                    continue;
                }

                var info = new FileInfo(file);
                files.Add(new FolderEntry(file, normalized, info.Length, info.LastWriteTimeUtc.Ticks));
            }
        }
        catch (Exception ex) when (IsRecoverableIo(ex))
        {
            context.SetFatal();
            context.AddError("folder.read-error", $"Dossier illisible : {ex.Message}", input.Path);
            return;
        }

        files.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.Ordinal));
        var fingerprint = ComputeFolderFingerprint(files);

        var existing = await _catalog.FindArchiveAsync(context.SpaceId, fingerprint, ct).ConfigureAwait(false);
        if (!context.IncludeChatMedia &&
            existing is not null &&
            string.Equals(existing.Status, "complete", StringComparison.Ordinal))
        {
            context.State.DuplicatesSkipped++;
            context.Reporter.FilesProcessed += files.Count;
            context.AddInfo(
                "archive.duplicate",
                "Export extrait déjà importé ; aucune nouvelle entrée n'a été créée.",
                input.Path);
            return;
        }

        var registration = await _catalog.RegisterArchiveAsync(
            context.BatchId,
            context.SpaceId,
            input.Path,
            fingerprint,
            files.Sum(file => file.Size),
            "processing",
            files.Count,
            ct).ConfigureAwait(false);

        if (!registration.Created && !context.IncludeChatMedia)
        {
            var current = await _catalog.FindArchiveAsync(context.SpaceId, fingerprint, ct).ConfigureAwait(false);
            if (current is not null &&
                string.Equals(current.Status, "complete", StringComparison.Ordinal))
            {
                context.State.DuplicatesSkipped++;
                context.Reporter.FilesProcessed += files.Count;
                context.AddInfo("archive.duplicate", "Export extrait déjà importé.", input.Path);
                return;
            }
        }
        else
        {
            context.State.ArchivesImported++;
        }

        var archiveFatal = false;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            context.State.EntriesSeen++;
            try
            {
                await ProcessFolderEntryAsync(file, registration.Id, context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableIo(ex))
            {
                archiveFatal = true;
                context.SetFatal();
                context.AddError(
                    "entry.io-error",
                    $"Fichier illisible : {ex.Message}",
                    input.Path,
                    file.RelativePath);
            }
            finally
            {
                context.Advance(file.RelativePath, ImportPhase.Storing);
            }
        }

        await _catalog.SetArchiveStatusAsync(
                registration.Id,
                archiveFatal ? "failed" : "complete",
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task ProcessFolderEntryAsync(
        FolderEntry file,
        long archiveId,
        ImportContext context,
        CancellationToken ct)
    {
        var normalized = file.RelativePath;
        var extension = ImportPaths.Extension(normalized);
        if (string.Equals(extension, "zip", StringComparison.OrdinalIgnoreCase))
        {
            context.State.Unrecognized++;
            context.AddWarning(
                "archive.nested",
                "Archive imbriquée ignorée : aucun adaptateur ne la reconnaît.",
                context.CurrentArchivePath,
                normalized);
            return;
        }

        if (normalized.StartsWith("chat_media/", StringComparison.OrdinalIgnoreCase))
        {
            if (!context.IncludeChatMedia || !SupportedExtensions.Contains(extension))
            {
                return;
            }

            await using var stream = new FileStream(
                file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await ProcessMediaAsync(
                stream,
                normalized,
                extension,
                MemoCatalog.RoleChat,
                null,
                archiveId,
                context,
                ct).ConfigureAwait(false);
            return;
        }

        if (!normalized.StartsWith("memories/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var match = MemoryEntryPattern.Match(normalized);
        if (!match.Success)
        {
            context.State.Unrecognized++;
            if (SupportedExtensions.Contains(extension))
            {
                context.AddWarning(
                    "entry.unrecognized",
                    "Fichier non reconnu dans memories/ ; il est ignoré.",
                    context.CurrentArchivePath,
                    normalized);
            }

            return;
        }

        var roleText = match.Groups["role"].Value.ToLowerInvariant();
        if (roleText == "overlay" && string.Equals(extension, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            context.State.Unrecognized++;
            context.AddWarning(
                "entry.role-format",
                "Un calque vidéo n'est pas reconnu.",
                context.CurrentArchivePath,
                normalized);
            return;
        }

        var role = roleText == "overlay" ? MediaRole.Overlay : MediaRole.Main;
        await using var mediaStream = new FileStream(
            file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await ProcessMediaAsync(
            mediaStream,
            normalized,
            extension,
            roleText,
            role,
            archiveId,
            context,
            ct).ConfigureAwait(false);
    }

    private async Task ProcessMediaAsync(
        Stream source,
        string entryPath,
        string declaredExtension,
        string roleText,
        MediaRole? role,
        long archiveId,
        ImportContext context,
        CancellationToken ct)
    {
        var importsDirectory = Path.Combine(_catalog.RootPath, "imports");
        Directory.CreateDirectory(importsDirectory);
        var temporary = Path.Combine(importsDirectory, $"import-{Guid.NewGuid():N}.tmp");
        try
        {
            var (sha, size) = await CopyAndHashAsync(source, temporary, ct).ConfigureAwait(false);

            MediaInfo info;
            DateSource exifSource;
            try
            {
                var inspection = await Task.Run(
                    () =>
                    {
                        using var stream = new FileStream(
                            temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                            FileOptions.SequentialScan);
                        var inspected = MediaInspector.Inspect(stream, out var sourceKind);
                        return (inspected, sourceKind);
                    },
                    ct).ConfigureAwait(false);
                info = inspection.inspected;
                exifSource = inspection.sourceKind;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.State.Unrecognized++;
                context.AddWarning(
                    "entry.inspect-error",
                    $"Métadonnées illisibles : {ex.Message}",
                    context.CurrentArchivePath,
                    entryPath);
                return;
            }

            if (string.Equals(info.Format, "unknown", StringComparison.Ordinal))
            {
                context.State.Unrecognized++;
                context.AddWarning(
                    "entry.format-unknown",
                    "Format binaire non reconnu ; le fichier n'est pas importé.",
                    context.CurrentArchivePath,
                    entryPath);
                return;
            }

            if (role == MediaRole.Overlay && MemoCatalog.IsVideoFormat(info.Format))
            {
                context.State.Unrecognized++;
                context.AddWarning(
                    "entry.role-format",
                    "Un calque ne peut pas être une vidéo.",
                    context.CurrentArchivePath,
                    entryPath);
                return;
            }

            var extension = MemoCatalog.CanonicalExtension(info.Format);
            var relativePath = $"media/{sha[..2]}/{sha}.{extension}";
            var finalPath = ImportPaths.ToPlatformPath(_catalog.RootPath, relativePath);
            var directory = Path.GetDirectoryName(finalPath)!;
            Directory.CreateDirectory(directory);

            if (File.Exists(finalPath))
            {
                TryDelete(temporary);
            }
            else
            {
                try
                {
                    File.Move(temporary, finalPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    TryDelete(temporary);
                }
            }

            var asset = await _catalog.EnsureAssetAsync(
                new AssetWrite(
                    sha,
                    info.Format,
                    info.Width,
                    info.Height,
                    info.Rotation,
                    info.DurationMs,
                    info.HasAudio,
                    relativePath,
                    size,
                    info.CreationTimeUtc,
                    info.ExifDate,
                    info.ExifDateRaw,
                    exifSource),
                ct).ConfigureAwait(false);

            var sourceFile = await _catalog.AddSourceFileAsync(
                context.SpaceId,
                archiveId,
                entryPath,
                sha,
                size,
                declaredExtension,
                info.Format,
                roleText,
                ct).ConfigureAwait(false);

            if (!sourceFile.Created)
            {
                context.State.DuplicatesSkipped++;
            }
            else if (role == MediaRole.Overlay)
            {
                context.State.OverlaysImported++;
            }
            else if (role == MediaRole.Main)
            {
                context.State.MediaImported++;
            }

            if (role is null)
            {
                // Média de conversation : conservé comme source, jamais rattaché à un souvenir.
                return;
            }

            var keyMatch = MemoryEntryPattern.Match(entryPath);
            if (!keyMatch.Success)
            {
                throw new InvalidDataException(
                    $"Le chemin « {entryPath} » ne contient pas d'identifiant de souvenir exploitable.");
            }

            var externalKey = keyMatch.Groups["uuid"].Value.ToLowerInvariant();
            var memory = await _catalog.EnsureMemoryAsync(context.SpaceId, externalKey, ct)
                .ConfigureAwait(false);
            if (memory.Created)
            {
                context.State.CreatedMemoryIds.Add(memory.Id);
            }
            else
            {
                context.State.UpdatedMemoryIds.Add(memory.Id);
            }

            context.Reporter.Report(
                ImportPhase.Associating,
                "Association du média et de son calque…",
                entryPath);
            await _catalog.LinkMemoryAsync(memory.Id, asset.Id, sourceFile.Id, role.Value, ct)
                .ConfigureAwait(false);

            await AddDateCandidatesAsync(memory.Id, info, exifSource, ct).ConfigureAwait(false);
            await _catalog.UpdateMemoryStatusAsync(memory.Id, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task AddDateCandidatesAsync(
        long memoryId,
        MediaInfo info,
        DateSource exifSource,
        CancellationToken ct)
    {
        if (info.ExifDate is not null)
        {
            var raw = info.ExifDateRaw ?? info.ExifDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var hasTime = TryExtractExifTime(raw, out var exifTime);
            var source = exifSource == DateSource.Unknown ? DateSource.ExifDateTime : exifSource;
            var confidence = source == DateSource.ExifDateTimeOriginal ? 0.95 : 0.6;
            await _catalog.EnsureDateCandidateAsync(
                memoryId,
                raw,
                source,
                DateMeaning.Capture,
                hasTime ? DatePrecision.Second : DatePrecision.Day,
                null,
                null,
                info.ExifDate,
                hasTime ? exifTime : null,
                confidence,
                ct).ConfigureAwait(false);
        }

        if (info.CreationTimeUtc is not null)
        {
            var timeZone = TimeZoneHelper.Resolve(_catalog.TimeZoneId);
            var (localDate, localTime) = DateText.Local(info.CreationTimeUtc.Value, timeZone);
            var instant = DateText.Instant(info.CreationTimeUtc.Value);
            await _catalog.EnsureDateCandidateAsync(
                memoryId,
                instant,
                DateSource.Mp4CreationTime,
                DateMeaning.ContainerCreated,
                DatePrecision.Second,
                timeZone.Id,
                instant,
                localDate,
                localTime,
                0.7,
                ct).ConfigureAwait(false);
        }
    }

    private static bool TryExtractExifTime(string raw, out TimeOnly time)
    {
        time = default;
        if (raw.Length < 19 || (raw[10] != ' ' && raw[10] != 'T'))
        {
            return false;
        }

        return TimeOnly.TryParseExact(
            raw.Substring(11, 8),
            "HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);
    }

    private static async Task<(string Sha256, long Size)> CopyAndHashAsync(
        Stream source,
        string destination,
        CancellationToken ct)
    {
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long size = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                size += read;
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(), size);
    }

    private static string ComputeFolderFingerprint(IReadOnlyList<FolderEntry> files)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var line = $"{file.RelativePath}\n{file.Size}\n{file.LastWriteUtcTicks}\n";
            hasher.AppendData(Encoding.UTF8.GetBytes(line));
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsRecoverableIo(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException
            or SqliteException
            or System.Security.SecurityException;

    private static void TryDelete(string path)
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

    private sealed record InputSource(string Path, bool IsZip);

    private sealed record FolderEntry(string FullPath, string RelativePath, long Size, long LastWriteUtcTicks);

    private sealed class CounterState
    {
        public int ArchivesSeen;
        public int ArchivesImported;
        public int EntriesSeen;
        public int MediaImported;
        public int OverlaysImported;
        public int DuplicatesSkipped;
        public int Unrecognized;
        public int Errors;
        public readonly HashSet<long> CreatedMemoryIds = new();
        public readonly HashSet<long> UpdatedMemoryIds = new();

        public ImportCounters Snapshot() => new(
            ArchivesSeen,
            ArchivesImported,
            EntriesSeen,
            MediaImported,
            OverlaysImported,
            CreatedMemoryIds.Count,
            UpdatedMemoryIds.Count,
            DuplicatesSkipped,
            Unrecognized,
            Errors);
    }

    private sealed class ProgressReporter
    {
        private readonly IProgress<ImportProgress>? _progress;
        private readonly CounterState _state;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReport = -1_000;
        private ImportPhase _phase = ImportPhase.Scanning;

        public ProgressReporter(IProgress<ImportProgress>? progress, CounterState state)
        {
            _progress = progress;
            _state = state;
        }

        public int FilesProcessed { get; set; }
        public int FilesTotal { get; set; }

        public void Report(ImportPhase phase, string message, string? current = null, bool force = false)
        {
            _phase = phase;
            if (_progress is null)
            {
                return;
            }

            var elapsed = _clock.ElapsedMilliseconds;
            if (!force && elapsed - _lastReport < 200)
            {
                return;
            }

            _lastReport = elapsed;
            try
            {
                _progress.Report(new ImportProgress(
                    _phase,
                    message,
                    FilesProcessed,
                    FilesTotal,
                    current,
                    _state.Snapshot()));
            }
            catch (Exception)
            {
                // Un collecteur de progression défaillant ne doit pas interrompre l'import.
            }
        }
    }

    private sealed class ImportContext
    {
        public ImportContext(
            MemoCatalog catalog,
            ImportRequest request,
            CounterState state,
            List<ImportDiagnostic> diagnostics,
            ProgressReporter reporter)
        {
            Catalog = catalog;
            Request = request;
            State = state;
            Diagnostics = diagnostics;
            Reporter = reporter;
        }

        public MemoCatalog Catalog { get; }
        public ImportRequest Request { get; }
        public CounterState State { get; }
        public List<ImportDiagnostic> Diagnostics { get; }
        public ProgressReporter Reporter { get; }
        public bool Fatal { get; private set; }
        public long SpaceId => Request.SpaceId;
        public bool IncludeChatMedia => Request.IncludeChatMedia;
        public long BatchId { get; set; }
        public string? CurrentArchivePath { get; set; }

        public void SetFatal() => Fatal = true;

        public void AddError(string code, string message, string? path = null, string? entryPath = null)
        {
            State.Errors++;
            Diagnostics.Add(new ImportDiagnostic("error", code, message, path, entryPath));
        }

        public void AddWarning(string code, string message, string? path = null, string? entryPath = null)
        {
            Diagnostics.Add(new ImportDiagnostic("warning", code, message, path, entryPath));
        }

        public void AddInfo(string code, string message, string? path = null, string? entryPath = null)
        {
            Diagnostics.Add(new ImportDiagnostic("info", code, message, path, entryPath));
        }

        public void Advance(string? current, ImportPhase phase)
        {
            Reporter.FilesProcessed++;
            Reporter.Report(phase, "Traitement des fichiers…", current);
        }
    }
}
