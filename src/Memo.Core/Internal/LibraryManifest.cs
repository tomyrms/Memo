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
}

internal static class LibraryManifest
{
    public const string FileName = "library.json";
    public const string FormatName = "memo-library";
    public const int CurrentVersion = 1;

    public static async Task<LibraryInfo> ReadAsync(string rootPath, CancellationToken ct = default)
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

        var timeZone = TimeZoneHelper.Resolve(document.TimeZoneId);
        return new LibraryInfo(document.Id, document.Name, root, timeZone.Id, document.CreatedAt);
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
        };
        await WriteAsync(rootPath, document, ct).ConfigureAwait(false);
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
