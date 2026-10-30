using SkiaSharp;

namespace Memo.Media;

/// <summary>Écriture de dérivés sans jamais toucher à la source : fichier temporaire puis remplacement.</summary>
internal static class AtomicFile
{
    public static void Write(string destinationPath, Action<Stream> writer)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Le chemin de destination est vide.", nameof(destinationPath));
        }

        ArgumentNullException.ThrowIfNull(writer);

        string fullPath = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.SequentialScan))
            {
                writer(stream);
                stream.Flush(flushToDisk: true);
            }

            // File.Move avec remplacement reste dans le même dossier, donc sur le même volume.
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static void WriteImage(SKBitmap bitmap, string destinationPath, SKEncodedImageFormat format, int quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        using var data = bitmap.Encode(format, quality);
        if (data is null || data.Size == 0)
        {
            throw new InvalidOperationException(
                $"L'encodage {format} de l'image a échoué ; aucun fichier n'a été produit.");
        }

        Write(destinationPath, stream => data.SaveTo(stream));
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
        catch (IOException)
        {
            // Un temporaire restant est sans effet sur la source ; le prochain passage le remplacera.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
