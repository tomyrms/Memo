using System.Globalization;
using Memo.Core;
using SkiaSharp;

namespace Memo.App.Export;

/// <summary>
/// Contrôle manuel de l'export vidéo. Le test fabrique une Story synthétique (une photo unie,
/// un calque PNG transparent, deux secondes) dans un dossier temporaire, lance le même
/// <see cref="StoryExporter"/> que l'interface, puis vérifie que le MP4 existe et dépasse 10 Ko.
///
/// Aucune donnée personnelle n'est lue : les images sont générées en mémoire. Le dossier et le
/// MP4 produits restent sur disque pour inspection manuelle ; l'appelant peut les supprimer.
/// </summary>
public static class StoryExportSmoke
{
    private const int Width = 1080;
    private const int Height = 1920;
    private const double PhotoSeconds = 2.0;
    private const long MinimumMp4Bytes = 10 * 1024;

    /// <summary>
    /// Exécute le contrôle et retourne un compte rendu lisible contenant le chemin du MP4.
    /// Lève <see cref="InvalidOperationException"/> avec le dossier de travail lorsque le
    /// fichier est absent, trop petit ou d'une durée incohérente.
    /// </summary>
    public static async Task<string> RunSelfCheckAsync(
        string tempDirectory,
        CancellationToken cancellationToken = default)
    {
        string root = string.IsNullOrWhiteSpace(tempDirectory)
            ? Path.GetTempPath()
            : tempDirectory;
        root = Path.GetFullPath(root);

        string directory = Path.Combine(
            root,
            "memo-export-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"Le contrôle d'export n'a pas pu créer son dossier de travail « {directory} » : " +
                ex.Message,
                ex);
        }

        string photoPath = Path.Combine(directory, "photo-synthetique.png");
        string overlayPath = Path.Combine(directory, "calque-synthetique.png");
        string outputPath = Path.Combine(directory, "story-synthetique.mp4");

        try
        {
            CreateSolidPhoto(photoPath);
            CreateTransparentOverlay(overlayPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"Le contrôle d'export n'a pas pu créer ses images synthétiques dans " +
                $"« {directory} » : {ex.Message}",
                ex);
        }

        var request = new StoryExportRequest(
            Items:
            [
                new StoryExportItem(
                    photoPath,
                    overlayPath,
                    MediaKind.Photo,
                    PhotoSeconds,
                    Width,
                    Height),
            ],
            OutputPath: outputPath,
            Title: "Contrôle d'export Memo",
            Quality: StoryExportQuality.Hd1080,
            IncludeAudio: false,
            PhotoDurationSeconds: PhotoSeconds,
            OverwriteExistingFile: true);

        StoryExportResult result;
        try
        {
            result = await new StoryExporter()
                .ExportAsync(request, progress: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"Le contrôle d'export a levé une exception. Dossier de travail : « {directory} ». " +
                ex.Message,
                ex);
        }

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputPath))
        {
            throw new InvalidOperationException(
                "Le contrôle d'export a échoué : " +
                (result.ErrorMessage ?? "cause inconnue") +
                $" Dossier de travail : « {directory} ».");
        }

        if (!File.Exists(result.OutputPath))
        {
            throw new InvalidOperationException(
                $"Le contrôle d'export annonce « {result.OutputPath} », mais le fichier est absent. " +
                $"Dossier de travail : « {directory} ».");
        }

        long size = new FileInfo(result.OutputPath).Length;
        if (size <= MinimumMp4Bytes)
        {
            throw new InvalidOperationException(
                $"Le MP4 de contrôle « {result.OutputPath} » ne fait que {size} octets " +
                $"(minimum attendu : plus de {MinimumMp4Bytes} octets). " +
                $"Dossier de travail : « {directory} ».");
        }

        MediaInfo produced;
        try
        {
            produced = MediaInspector.InspectFile(result.OutputPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"Le MP4 de contrôle « {result.OutputPath} » n'a pas pu être relu : {ex.Message}. " +
                $"Dossier de travail : « {directory} ».",
                ex);
        }

        if (produced.Width != Width || produced.Height != Height)
        {
            throw new InvalidOperationException(
                $"Le MP4 de contrôle « {result.OutputPath} » annonce " +
                $"{produced.Width?.ToString() ?? "?"}×{produced.Height?.ToString() ?? "?"}, " +
                $"au lieu de {Width}×{Height}. Dossier de travail : « {directory} ».");
        }

        if (result.Duration < TimeSpan.FromSeconds(1) ||
            result.Duration > TimeSpan.FromSeconds(4))
        {
            throw new InvalidOperationException(
                $"Le MP4 de contrôle dure {result.Duration.TotalSeconds:0.00} s, alors que la " +
                $"photo synthétique devait durer {PhotoSeconds:0.00} s. " +
                $"Dossier de travail : « {directory} ».");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Contrôle d'export réussi : « {result.OutputPath} » " +
            $"({FormatSize(size)}, {result.Duration.TotalSeconds:0.00} s, photo 1080×1920 avec calque).");
    }

    private static void CreateSolidPhoto(string path)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            Width,
            Height,
            SKColorType.Bgra8888,
            SKAlphaType.Opaque));
        bitmap.Erase(new SKColor(0x1F, 0x4E, 0x6D));
        SavePng(bitmap, path);
    }

    private static void CreateTransparentOverlay(string path)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            Width,
            Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);

        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint
               {
                   Color = new SKColor(0xFF, 0xFF, 0xFF, 0xA0),
                   IsAntialias = true,
               })
        {
            // Un disque translucide suffit à distinguer un calque réellement composé d'un fond uni.
            canvas.DrawCircle(Width / 2f, Height / 2f, Width * 0.28f, paint);
            canvas.Flush();
        }

        SavePng(bitmap, path);
    }

    private static void SavePng(SKBitmap bitmap, string path)
    {
        using SKData? data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        if (data is null || data.Size == 0)
        {
            throw new InvalidOperationException(
                $"L'encodage PNG de contrôle a produit un fichier vide pour « {path} ».");
        }

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        data.SaveTo(stream);
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024
        ? (bytes / (1024.0 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " Mo"
        : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " Ko";
}
