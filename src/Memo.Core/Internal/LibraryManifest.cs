using System.Text.Json;

namespace Memo.Core;

internal sealed class LibraryDocument
{
    public string? Format { get; set; }
    public int Version { get; set; }
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? TimeZoneId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Dernière écriture connue du manifeste (version 2).</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Manifeste de bibliothèque (<c>library.json</c>), versionné indépendamment du
/// catalogue.
///
/// Version 1 : format livré avec Memo 0.1.0.
/// Version 2 : ajoute <c>updatedAt</c>, date de dernière migration ou mise à
/// jour du manifeste. Une version 1 est réécrite en version 2 à l'ouverture,
/// de façon idempotente. Un manifeste plus récent que l'application est refusé.
/// </summary>
internal static class LibraryManifest
{
    public const string FileName = "library.json";
    public const string FormatName = "memo-library";
    public const int CurrentVersion = 2;

    public static async Task<LibraryInfo> ReadAsync(string rootPath, CancellationToken ct = default)
    {
        var (root, document) = await ReadDocumentAsync(rootPath, ct).ConfigureAwait(false);
        Validate(document, root);
        return new LibraryInfo(
            document.Id,
            document.Name!,
            root,
            TimeZoneHelper.Resolve(document.TimeZoneId).Id,
            document.CreatedAt);
    }

    /// <summary>
    /// Réécrit un manifeste version 1 dans le format courant. Sans effet si le
    /// manifeste est déjà à jour ; refuse toujours une version plus récente.
    /// </summary>
    public static async Task EnsureCurrentVersionAsync(string rootPath, CancellationToken ct = default)
    {
        var (root, document) = await ReadDocumentAsync(rootPath, ct).ConfigureAwait(false);
        Validate(document, root);
        if (document.Version >= CurrentVersion)
        {
            return;
        }

        // La date de création est la seule date connue pour un manifeste 0.1.0.
        document.UpdatedAt ??= document.CreatedAt;
        document.Version = CurrentVersion;
        await WriteAsync(root, document, ct).ConfigureAwait(false);
    }

    public static async Task WriteAsync(string rootPath, LibraryDocument document, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        var temporary = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonSupport.Options, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                TryDelete(temporary);
            }
        }
    }

    public static async Task UpdateTimeZoneAsync(string rootPath, string timeZoneId, CancellationToken ct = default)
    {
        var info = await ReadAsync(rootPath, ct).ConfigureAwait(false);
        var document = new LibraryDocument
        {
            Format = FormatName,
            Version = CurrentVersion,
            Id = info.Id,
            Name = info.Name,
            TimeZoneId = timeZoneId,
            CreatedAt = info.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await WriteAsync(rootPath, document, ct).ConfigureAwait(false);
    }

    private static async Task<(string Root, LibraryDocument Document)> ReadDocumentAsync(
        string rootPath,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(rootPath);
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Aucune bibliothèque Memo reconnue dans « {root} » : le fichier {FileName} est absent.");
        }

        LibraryDocument? document;
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonSerializer.DeserializeAsync<LibraryDocument>(stream, JsonSupport.Options, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Le fichier {FileName} de « {root} » est illisible : {ex.Message}", ex);
        }

        if (document is null)
        {
            throw new InvalidOperationException($"Le fichier {FileName} de « {root} » est vide.");
        }

        return (root, document);
    }

    private static void Validate(LibraryDocument document, string root)
    {
        if (!string.Equals(document.Format, FormatName, StringComparison.Ordinal))
        {
            var actual = string.IsNullOrWhiteSpace(document.Format) ? "(absent)" : document.Format;
            throw new InvalidOperationException(
                $"Bibliothèque inconnue dans « {root} » : format « {actual} » non reconnu.");
        }

        if (document.Version > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"La bibliothèque « {root} » utilise un format plus récent (version {document.Version}). " +
                "Une version plus récente de Memo est nécessaire.");
        }

        if (document.Version <= 0)
        {
            throw new InvalidOperationException(
                $"Bibliothèque invalide dans « {root} » : version de format {document.Version}.");
        }

        if (document.Id == Guid.Empty)
        {
            throw new InvalidOperationException($"Bibliothèque invalide dans « {root} » : identifiant absent.");
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            throw new InvalidOperationException($"Bibliothèque invalide dans « {root} » : nom absent.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
