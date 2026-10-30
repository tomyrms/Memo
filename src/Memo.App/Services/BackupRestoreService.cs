using Memo.Core;

namespace Memo.App.Services;

/// <summary>
/// Restauration d'une sauvegarde indépendante de la bibliothèque ouverte.
/// <para>
/// <see cref="BackupService"/> est construit autour d'un catalogue ouvert alors
/// que sa restauration ne lit ni n'écrit dans ce catalogue. Pour réutiliser la
/// validation et l'extraction de Memo.Core sans modifier Core, on ouvre une
/// bibliothèque jetable dans le dossier temporaire, uniquement destinée à
/// fournir le service. Elle est supprimée à la fin, en succès comme en échec.
/// </para>
/// </summary>
internal static class BackupRestoreService
{
    public static async Task RestoreAsync(
        string backupZip,
        string destinationRoot,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backupZip))
        {
            throw new ArgumentException("Le chemin de la sauvegarde est obligatoire.", nameof(backupZip));
        }
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new ArgumentException("Le dossier de restauration est obligatoire.", nameof(destinationRoot));
        }

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "memo",
            $"restore-host-{Guid.NewGuid():N}");

        try
        {
            await using var host = await MemoLibrary.CreateAsync(
                temporaryRoot,
                "Restauration temporaire",
                "UTC",
                ct);

            // La sauvegarde est extraite et vérifiée dans un dossier frère de la
            // destination ; un échec ne modifie donc pas la bibliothèque en cours.
            await host.Backup.RestoreAsync(backupZip, destinationRoot, progress, ct);
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
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
            // Nettoyage best-effort du dossier temporaire.
        }
        catch (UnauthorizedAccessException)
        {
            // Nettoyage best-effort du dossier temporaire.
        }
    }
}
