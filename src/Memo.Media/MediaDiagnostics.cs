using System.Text;
using LibVLCSharp.Shared;
using SkiaSharp;

namespace Memo.Media;

/// <summary>Une dépendance du moteur média et son état, prêt à afficher dans les Réglages.</summary>
public sealed record MediaDiagnosticItem(string Key, string Label, bool Available, string Detail);

/// <summary>Rapport lisible des dépendances chargées au démarrage.</summary>
public sealed class MediaDiagnosticsReport
{
    public MediaDiagnosticsReport(IReadOnlyList<MediaDiagnosticItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToArray();
    }

    public IReadOnlyList<MediaDiagnosticItem> Items { get; }

    public int AvailableCount => Items.Count(item => item.Available);

    public bool IsHealthy => Items.Count > 0 && Items.All(item => item.Available);

    public string Summary
    {
        get
        {
            if (Items.Count == 0)
            {
                return "Moteur média : aucun contrôle n'a été exécuté.";
            }

            if (IsHealthy)
            {
                return $"Moteur média prêt : {AvailableCount} dépendance(s) disponible(s).";
            }

            int missing = Items.Count - AvailableCount;
            return
                $"Moteur média : {missing} dépendance(s) indisponible(s) sur {Items.Count}. " +
                "Les médias restent consultables, mais certaines miniatures ou lectures peuvent manquer.";
        }
    }

    /// <summary>Texte multi-lignes destiné à l'écran Réglages.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Summary);
        foreach (var item in Items)
        {
            builder.Append("- ");
            builder.Append(item.Label);
            builder.Append(" : ");
            builder.Append(item.Available ? "disponible" : "indisponible");
            builder.Append(" — ");
            builder.AppendLine(item.Detail);
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// Contrôles de démarrage : décodage WebP par SkiaSharp et chargement de libvlc. Ces vérifications
/// sont locales, sans réseau, et ne dépendent pas des extensions Windows préinstallées.
/// </summary>
public static class MediaDiagnostics
{
    public static MediaDiagnosticsReport Check()
    {
        var items = new List<MediaDiagnosticItem>(2)
        {
            CheckSkiaWebP(),
            CheckLibVlc(),
        };

        return new MediaDiagnosticsReport(items);
    }

    private static MediaDiagnosticItem CheckSkiaWebP()
    {
        const string key = "skia-webp";
        const string label = "Images — décodage WebP (SkiaSharp)";

        try
        {
            using var source = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    source.SetPixel(x, y, SKColors.Red);
                }
            }

            using var encoded = source.Encode(SKEncodedImageFormat.Webp, 90);
            if (encoded is null || encoded.Size == 0)
            {
                return new MediaDiagnosticItem(key, label, false, "L'encodage WebP de contrôle a produit un fichier vide.");
            }

            using var decoded = SKBitmap.Decode(encoded);
            if (decoded is null || decoded.Width != 8 || decoded.Height != 8)
            {
                return new MediaDiagnosticItem(
                    key,
                    label,
                    false,
                    "Le WebP de contrôle n'a pas pu être relu aux dimensions attendues (8×8).");
            }

            SKColor red = decoded.GetPixel(4, 4);
            bool redOk = red.Red > 180 && red.Green < 80 && red.Blue < 80;
            return new MediaDiagnosticItem(
                key,
                label,
                redOk,
                redOk
                    ? "WebP décodé (contrôle 8×8, pixel rouge conforme)."
                    : "Le WebP a été lu mais le pixel de contrôle est incohérent.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new MediaDiagnosticItem(
                key,
                label,
                false,
                $"Échec du décodage WebP : {ex.GetBaseException().Message}");
        }
    }

    private static MediaDiagnosticItem CheckLibVlc()
    {
        const string key = "libvlc";
        const string label = "Vidéo — moteur libvlc";

        try
        {
            VlcRuntime.EnsureInitialized();
            using var libVlc = new LibVLC("--no-audio", "--quiet");
            string version = string.IsNullOrWhiteSpace(libVlc.Version) ? "version inconnue" : libVlc.Version;
            return new MediaDiagnosticItem(key, label, true, $"libvlc {version} chargé.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new MediaDiagnosticItem(
                key,
                label,
                false,
                $"libvlc n'a pas pu être chargé : {ex.GetBaseException().Message}");
        }
    }
}
