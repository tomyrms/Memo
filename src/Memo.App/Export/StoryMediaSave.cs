using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Memo.Core;

namespace Memo.App.Export;

/// <summary>
/// Demande d'enregistrement du média courant. La copie est toujours octet pour octet :
/// aucun décodage, aucune réécriture et donc aucun réencodage ne sont effectués. Le calque
/// éventuel est copié à côté du média, dans un fichier distinct.
/// </summary>
public sealed record StoryMediaSaveRequest(
    string SourcePath,
    string DestinationPath,
    string? OverlaySourcePath,
    string Kind,
    string DateLabel,
    string? TimeLabel,
    DatePrecision DatePrecision,
    DateSource DateSource,
    DateMeaning DateMeaning,
    int ItemIndex,
    int ItemCount,
    string LibraryName,
    string SpaceName,
    string? LocationLabel,
    long? Width,
    long? Height,
    long? DurationMs,
    string? Format);

/// <summary>Résultat d'un enregistrement : chemins réellement écrits et empreintes SHA-256.</summary>
public sealed record StoryMediaSaveResult(
    string MediaPath,
    long MediaBytes,
    string MediaSha256,
    string? OverlayPath,
    long? OverlayBytes,
    string? OverlaySha256,
    string MetadataPath);

/// <summary>
/// Enregistre un média source et son calque sans passer par Media Foundation ni par un
/// encodeur. Les métadonnées JSON sont écrites dans un fichier annexe à côté du média.
/// </summary>
public static class StoryMediaSaver
{
    private const int CopyBufferSize = 81920;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Copie le média vers <see cref="StoryMediaSaveRequest.DestinationPath"/>, copie le calque
    /// dans le même dossier s'il existe, puis écrit un JSON de métadonnées. Une annulation ou une
    /// erreur de copie supprime uniquement le fichier partiel concerné ; elle ne touche jamais
    /// aux sources.
    /// </summary>
    public static async Task<StoryMediaSaveResult> SaveAsync(
        StoryMediaSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            throw new InvalidOperationException("Le média source est introuvable.");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            throw new InvalidOperationException("Le fichier de destination est vide.");
        }

        string source = Path.GetFullPath(request.SourcePath);
        string destination = Path.GetFullPath(request.DestinationPath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                $"Le média source « {source} » est introuvable.",
                source);
        }

        if (PathsEqual(source, destination))
        {
            throw new InvalidOperationException(
                "La destination désigne le média source lui-même. Choisissez un autre fichier " +
                "pour ne jamais modifier l'original.");
        }

        CopiedFile media;
        CopiedFile? overlay = null;
        try
        {
            // Le calque est copié en premier : il est petit, et un échec de la copie
            // principale ne laisse ainsi jamais un calque orphelin.
            if (!string.IsNullOrWhiteSpace(request.OverlaySourcePath) &&
                File.Exists(request.OverlaySourcePath))
            {
                string overlaySource = Path.GetFullPath(request.OverlaySourcePath);
                string overlayDestination = BuildOverlayDestination(destination, overlaySource);
                overlay = await CopyWithHashAsync(overlaySource, overlayDestination, cancellationToken)
                    .ConfigureAwait(false);
            }

            media = await CopyWithHashAsync(source, destination, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (overlay is not null)
            {
                TryDelete(overlay.Path);
            }

            throw;
        }

        string metadataPath = BuildMetadataPath(destination);
        var metadata = new
        {
            schemaVersion = 1,
            kind = "memo-media-save",
            reencoded = false,
            copyMode = "octet-pour-octet",
            savedAt = DateTimeOffset.Now,
            story = new
            {
                index = request.ItemIndex,
                count = request.ItemCount,
                library = request.LibraryName,
                space = request.SpaceName,
            },
            memory = new
            {
                kind = request.Kind,
                date = request.DateLabel,
                exactTime = request.TimeLabel,
                datePrecision = request.DatePrecision.ToString(),
                dateSource = request.DateSource.ToString(),
                dateMeaning = request.DateMeaning.ToString(),
                location = request.LocationLabel,
                width = request.Width,
                height = request.Height,
                durationMs = request.DurationMs,
                format = request.Format,
            },
            media = new
            {
                fileName = Path.GetFileName(media.Path),
                originalFileName = Path.GetFileName(source),
                bytes = media.Bytes,
                sha256 = media.Sha256,
            },
            overlay = overlay is null
                ? null
                : new
                {
                    fileName = Path.GetFileName(overlay.Path),
                    originalFileName = Path.GetFileName(request.OverlaySourcePath),
                    bytes = overlay.Bytes,
                    sha256 = overlay.Sha256,
                },
        };

        try
        {
            string json = JsonSerializer.Serialize(metadata, JsonOptions);
            await File.WriteAllTextAsync(metadataPath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryDelete(metadataPath);
            throw new IOException(
                $"Le média a été copié vers « {media.Path} », mais les métadonnées JSON " +
                $"« {metadataPath} » n'ont pas pu être écrites : {Describe(ex)}",
                ex);
        }

        return new StoryMediaSaveResult(
            media.Path,
            media.Bytes,
            media.Sha256,
            overlay?.Path,
            overlay?.Bytes,
            overlay?.Sha256,
            metadataPath);
    }

    /// <summary>
    /// Extension exploitable pour le sélecteur de fichiers. Une extension absente ou suspecte
    /// est remplacée par <paramref name="fallbackExtension"/>.
    /// </summary>
    public static string GetSafeExtension(string? path, string fallbackExtension)
    {
        string fallback = NormalizeExtension(fallbackExtension);
        if (string.IsNullOrWhiteSpace(path))
        {
            return fallback;
        }

        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return fallback;
        }

        if (extension.Length is < 2 or > 12)
        {
            return fallback;
        }

        for (int i = 1; i < extension.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(extension[i]))
            {
                return fallback;
            }
        }

        return extension.ToLowerInvariant();
    }

    /// <summary>Libellé lisible du type de fichier, utilisé par le FileSavePicker.</summary>
    public static string GetFileTypeLabel(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "Vidéo MP4",
        ".mov" => "Vidéo QuickTime",
        ".avi" => "Vidéo AVI",
        ".mkv" => "Vidéo Matroska",
        ".webm" => "Vidéo WebM",
        ".jpg" or ".jpeg" => "Image JPEG",
        ".png" => "Image PNG",
        ".webp" => "Image WebP",
        ".heic" or ".heif" => "Image HEIC",
        ".gif" => "Image GIF",
        ".bmp" => "Image BMP",
        ".tif" or ".tiff" => "Image TIFF",
        _ => $"Fichier {extension}",
    };

    private static async Task<CopiedFile> CopyWithHashAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PathsEqual(source, destination))
        {
            throw new InvalidOperationException(
                "La destination désigne le fichier source lui-même ; la copie est refusée " +
                "pour ne jamais modifier l'original.");
        }

        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException(
                $"Le dossier de destination « {destination} » est invalide.");
        }

        Directory.CreateDirectory(directory);

        // La copie est écrite dans un fichier temporaire du même dossier, puis renommée
        // seulement lorsqu'elle est complète. Un échec ou une annulation ne détruit donc
        // jamais un fichier de destination déjà présent.
        string temporary = Path.Combine(directory, $".memo-copy-{Guid.NewGuid():N}.tmp");
        long total = 0;
        string sha256;

        try
        {
            using (var input = new FileStream(
                       source,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       CopyBufferSize,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       CopyBufferSize,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                try
                {
                    int read;
                    while ((read = await input
                               .ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await output
                            .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        total += read;
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                sha256 = Convert
                    .ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant();
            }

            File.Move(temporary, destination, overwrite: true);

            try
            {
                // Conserver la date de modification facilite le rapprochement avec l'original ;
                // le contenu, lui, reste strictement identique.
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Un horodatage refusé ne remet pas en cause la copie.
            }

            return new CopiedFile(destination, total, sha256);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static string BuildOverlayDestination(string mainDestination, string overlaySource)
    {
        string directory = Path.GetDirectoryName(mainDestination)
                           ?? throw new InvalidOperationException(
                               $"Le dossier de destination « {mainDestination} » est invalide.");
        string stem = Path.GetFileNameWithoutExtension(mainDestination);
        string extension = GetSafeExtension(overlaySource, ".png");
        return MakeUniquePath(Path.Combine(directory, $"{stem}-calque{extension}"));
    }

    private static string BuildMetadataPath(string destination) =>
        MakeUniquePath(destination + ".json");

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string? directory = Path.GetDirectoryName(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int index = 2; index < 1000; index++)
        {
            string candidate = Path.Combine(directory ?? string.Empty, $"{stem}-{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Aucun nom de fichier libre n'a pu être trouvé pour « {path} ».");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".bin";
        }

        return extension.StartsWith('.')
            ? extension.ToLowerInvariant()
            : "." + extension.TrimStart('.').ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un fichier partiel verrouillé sera signalé par le message d'erreur principal.
        }
    }

    private static string Describe(Exception exception)
    {
        Exception root = exception.GetBaseException();
        return string.IsNullOrWhiteSpace(root.Message) ? root.GetType().Name : root.Message;
    }

    private sealed record CopiedFile(string Path, long Bytes, string Sha256);
}
