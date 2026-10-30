namespace Memo.TestSupport;

/// <summary>
/// Dossier temporaire jetable pour les tests et la génération de corpus.
/// Le nettoyage est tolérant aux fichiers encore verrouillés par un lecteur.
/// </summary>
public sealed class TempLibrary : IDisposable, IAsyncDisposable
{
    private bool _disposed;

    public TempLibrary(string prefix = "memo-test-")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        Root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    /// <summary>Racine du dossier temporaire.</summary>
    public string Root { get; }

    /// <summary>Chemin absolu à partir d'un chemin relatif.</summary>
    public string Combine(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var all = new string[parts.Length + 1];
        all[0] = Root;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    /// <summary>Crée un sous-dossier et retourne son chemin absolu.</summary>
    public string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Écrit un fichier binaire et retourne son chemin absolu.</summary>
    public string WriteFile(string relativePath, ReadOnlySpan<byte> content)
    {
        var path = Path.Combine(Root, relativePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(content);
        return path;
    }

    /// <summary>Écrit un fichier texte UTF-8 et retourne son chemin absolu.</summary>
    public string WriteText(string relativePath, string content) =>
        WriteFile(relativePath, System.Text.Encoding.UTF8.GetBytes(content));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DeleteWithRetry(Root);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    ClearReadOnly(path);
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(40 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(40 * (attempt + 1));
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static void ClearReadOnly(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (IOException)
            {
                // Le nettoyage reprendra au prochain essai.
            }
        }
    }
}
