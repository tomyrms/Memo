using System.Numerics;
using Memo.Media;
using SkiaSharp;

namespace Memo.App.Analysis;

/// <summary>Correspondance trouvée par <see cref="VisualHashService.FindSimilar"/>.</summary>
public readonly record struct SimilarImage(long MemoryId, ulong Hash, int Distance);

/// <summary>
/// Empreinte perceptuelle dHash 64 bits, calculée localement avec SkiaSharp.
///
/// L'image est ramenée à 9 × 8 niveaux de gris, puis chaque bit compare deux pixels voisins
/// horizontaux : le bit est à 1 quand le pixel de droite est plus clair. Deux images visuellement
/// proches donnent des empreintes proches, même après un recadrage léger, un changement de
/// compression ou une réduction de taille.
///
/// Les miniatures déjà présentes dans la bibliothèque suffisent : il est inutile de relire les
/// originaux, souvent volumineux, pour calculer ou comparer une empreinte.
/// </summary>
public static class VisualHashService
{
    /// <summary>Largeur de la grille : 9 colonnes produisent 8 comparaisons par ligne.</summary>
    public const int HashWidth = 9;

    /// <summary>Hauteur de la grille : 8 lignes produisent 8 bits par colonne.</summary>
    public const int HashHeight = 8;

    /// <summary>
    /// Distance de Hamming par défaut pour « image similaire ». Sur 64 bits, 10 bits d'écart
    /// (environ 16 %) accepte une recompression ou un léger recadrage sans confondre deux scènes.
    /// </summary>
    public const int DefaultMaxDistance = 10;

    /// <summary>Taille intermédiaire utilisée avant la grille finale, pour limiter l'aliasing.</summary>
    private const int IntermediateMaxDimension = 72;

    /// <summary>Calcule l'empreinte d'un fichier JPEG, PNG ou WebP.</summary>
    /// <exception cref="Memo.Media.MediaDecodeException">Le fichier n'est pas une image décodable.</exception>
    public static ulong ComputeFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var decoded = ImageDecoder.Decode(path);
        return ComputeFromBitmap(decoded.Bitmap);
    }

    /// <summary>Calcule l'empreinte de données encodées déjà en mémoire.</summary>
    /// <exception cref="Memo.Media.MediaDecodeException">Les données ne sont pas une image décodable.</exception>
    public static ulong ComputeFromBytes(ReadOnlySpan<byte> encodedData)
    {
        using var decoded = ImageDecoder.Decode(encodedData);
        return ComputeFromBitmap(decoded.Bitmap);
    }

    /// <summary>
    /// Variante sans exception : utile pour une passe en lot où un fichier corrompu ne doit pas
    /// interrompre les suivants. Le message d'erreur est en français et nomme le fichier.
    /// </summary>
    public static bool TryComputeFromFile(string path, out ulong hash, out string? error)
    {
        hash = 0;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Aucun chemin d'image n'a été fourni pour l'empreinte visuelle.";
            return false;
        }

        try
        {
            hash = ComputeFromFile(path);
            error = null;
            return true;
        }
        catch (MediaDecodeException ex)
        {
            error = $"Empreinte visuelle impossible pour « {path} » : {ex.Message}";
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"L'empreinte visuelle de « {path} » a échoué : {ex.Message}";
            return false;
        }
    }

    /// <summary>Calcule l'empreinte d'une image déjà décodée, entière.</summary>
    public static ulong ComputeFromBitmap(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return ComputeFromRegion(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
    }

    /// <summary>
    /// Calcule l'empreinte d'une région précise. Le redimensionnement est fait sur un carré ou un
    /// rectangle fourni par l'appelant (par exemple le cadre d'un visage).
    /// </summary>
    public static ulong ComputeFromRegion(SKBitmap bitmap, SKRectI region)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            throw new ArgumentException("L'image source est vide.", nameof(bitmap));
        }

        int left = Math.Clamp(region.Left, 0, bitmap.Width - 1);
        int top = Math.Clamp(region.Top, 0, bitmap.Height - 1);
        int right = Math.Clamp(region.Right, left + 1, bitmap.Width);
        int bottom = Math.Clamp(region.Bottom, top + 1, bitmap.Height);
        var sourceRect = new SKRect(left, top, right, bottom);

        // Premier passage vers une taille intermédiaire : un unique saut de 4000 px à 9 px
        // échantillonne trop peu de pixels et rend l'empreinte sensible au bruit.
        double scale = Math.Min(
            1.0,
            IntermediateMaxDimension / (double)Math.Max(sourceRect.Width, sourceRect.Height));
        int intermediateWidth = Math.Max(HashWidth, (int)Math.Round(sourceRect.Width * scale));
        int intermediateHeight = Math.Max(HashHeight, (int)Math.Round(sourceRect.Height * scale));

        var info = new SKImageInfo(
            intermediateWidth,
            intermediateHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Opaque);
        using var intermediate = new SKBitmap(info);
        using (var canvas = new SKCanvas(intermediate))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(
                bitmap,
                sourceRect,
                new SKRect(0, 0, intermediateWidth, intermediateHeight),
                new SKSamplingOptions(SKCubicResampler.Mitchell),
                paint);
            canvas.Flush();
        }

        var hashInfo = new SKImageInfo(HashWidth, HashHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var small = new SKBitmap(hashInfo);
        using (var canvas = new SKCanvas(small))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(
                intermediate,
                new SKRect(0, 0, intermediate.Width, intermediate.Height),
                new SKRect(0, 0, HashWidth, HashHeight),
                new SKSamplingOptions(SKCubicResampler.Mitchell),
                paint);
            canvas.Flush();
        }

        return HashFromPixels(small);
    }

    /// <summary>Distance de Hamming entre deux empreintes (0 = identiques, 64 = opposées).</summary>
    public static int HammingDistance(ulong left, ulong right) => BitOperations.PopCount(left ^ right);

    /// <summary>
    /// Compare une empreinte à une liste d'empreintes existantes et retourne les souvenirs dont la
    /// distance est inférieure ou égale à <paramref name="maxDistance"/>, du plus proche au plus
    /// lointain. Les candidats viennent typiquement de
    /// <c>AnalysisStore.GetVisualHashesAsync</c>.
    /// </summary>
    public static IReadOnlyList<SimilarImage> FindSimilar(
        ulong hash,
        IEnumerable<(long MemoryId, ulong Hash)> candidates,
        int maxDistance = DefaultMaxDistance)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (maxDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "La distance maximale ne peut pas être négative.");
        }

        var matches = new List<SimilarImage>();
        foreach (var (memoryId, candidateHash) in candidates)
        {
            int distance = HammingDistance(hash, candidateHash);
            if (distance <= maxDistance)
            {
                matches.Add(new SimilarImage(memoryId, candidateHash, distance));
            }
        }

        matches.Sort(static (left, right) =>
        {
            int byDistance = left.Distance.CompareTo(right.Distance);
            return byDistance != 0 ? byDistance : left.MemoryId.CompareTo(right.MemoryId);
        });
        return matches;
    }

    private static ulong HashFromPixels(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        // 9 × 8 = 72 luminances : assez petit pour rester sur la pile.
        Span<int> luminance = stackalloc int[HashWidth * HashHeight];
        for (int y = 0; y < HashHeight; y++)
        {
            int row = y * stride;
            for (int x = 0; x < HashWidth; x++)
            {
                int offset = row + (x * 4);
                int b = pixels[offset];
                int g = pixels[offset + 1];
                int r = pixels[offset + 2];
                luminance[(y * HashWidth) + x] = ((r * 299) + (g * 587) + (b * 114)) / 1000;
            }
        }

        ulong hash = 0;
        for (int y = 0; y < 8; y++)
        {
            int row = y * HashWidth;
            for (int x = 0; x < 8; x++)
            {
                if (luminance[row + x + 1] > luminance[row + x])
                {
                    hash |= 1UL << ((y * 8) + x);
                }
            }
        }

        return hash;
    }
}
