using System.Diagnostics;
using Memo.Media;
using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;
using WinOcrResult = Windows.Media.Ocr.OcrResult;

namespace Memo.App.Analysis;

/// <summary>Mot reconnu et son rectangle normalisé (0..1) dans le sens d'affichage.</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);

/// <summary>Ligne de texte reconnue et ses mots.</summary>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

/// <summary>
/// Résultat d'une passe OCR. <see cref="Error"/> reste nul quand l'image a été lue, même si aucun
/// texte n'a été trouvé. <see cref="EngineUnavailable"/> distingue l'absence de pack de langue
/// d'une simple image sans texte : l'appelant peut alors arrêter la passe au lieu de répéter le
/// même échec sur chaque fichier.
/// </summary>
public sealed record OcrResult(
    string Text,
    IReadOnlyList<OcrLine> Lines,
    string? Language,
    TimeSpan Duration,
    string? Error,
    bool Cancelled = false,
    bool EngineUnavailable = false)
{
    /// <summary>Vrai si l'image a été lue et que l'analyse n'a pas été annulée.</summary>
    public bool Succeeded => Error is null && !Cancelled;

    public static OcrResult Failed(string error, TimeSpan duration, bool engineUnavailable = false) =>
        new(string.Empty, Array.Empty<OcrLine>(), null, duration, error, false, engineUnavailable);

    public static OcrResult CreateCancelled(TimeSpan duration, string? language = null) =>
        new(string.Empty, Array.Empty<OcrLine>(), language, duration, "Analyse annulée.", true);
}

/// <summary>
/// OCR local basé sur <c>Windows.Media.Ocr.OcrEngine</c>. Aucun octet d'image ne quitte la machine.
///
/// Le décodage passe par SkiaSharp (JPEG, PNG et WebP), applique l'orientation EXIF puis convertit
/// l'image en BGRA compact pour <c>SoftwareBitmap</c>. On ne dépend donc pas du codec WebP de
/// Windows, absent de certaines installations.
///
/// L'analyse est facultative : une machine sans pack de langue OCR, sans moteur Windows ou avec une
/// image illisible retourne un <see cref="OcrResult"/> en erreur, jamais une exception non gérée.
/// Seule l'annulation volontaire est signalée par <see cref="OcrResult.Cancelled"/> après nettoyage.
/// </summary>
public sealed class LocalOcrService
{
    /// <summary>
    /// Plus grand côté envoyé au moteur OCR. Windows accepte jusqu'à 10 000 px, mais 2 000 px
    /// conservent la lisibilité du texte des photos tout en réduisant fortement la mémoire et le temps.
    /// </summary>
    public const int MaxImageDimension = 2000;

    /// <summary>Identifiant du moteur écrit dans <c>analysis_ocr.engine</c>, suivi de la langue.</summary>
    public const string EngineId = "windows-ocr";

    /// <summary>Ordre de repli quand les langues du profil utilisateur ne fournissent pas de moteur.</summary>
    private static readonly string[] FallbackLanguageTags = ["fr-FR", "en-US", "en-GB"];

    /// <summary>Message commun quand aucun pack OCR n'est utilisable.</summary>
    public const string MissingLanguagePackMessage =
        "Aucun moteur OCR n'est installé pour les langues de Windows. " +
        "Ajoutez « Reconnaissance optique de caractères » dans Paramètres > Heure et langue > " +
        "Langue et région > (langue) > Options de langue, puis relancez Memo. " +
        "L'OCR a aussi essayé les langues fr-FR, en-US et en-GB.";

    private static readonly object EngineGate = new();
    private static readonly SemaphoreSlim RecognizeGate = new(1, 1);
    private static WinOcrEngine? _engine;
    private static string? _engineError;
    private static bool _probed;

    /// <summary>
    /// Oublie le moteur mémorisé. Utile après l'installation d'un pack de langue, sans redémarrer
    /// l'application, ou depuis les tests.
    /// </summary>
    public static void ResetEngineCache()
    {
        lock (EngineGate)
        {
            _engine = null;
            _engineError = null;
            _probed = false;
        }
    }

    /// <summary>
    /// Message d'indisponibilité, ou <c>null</c> si un moteur OCR est disponible. Crée le moteur au
    /// premier appel, comme <see cref="RecognizeAsync"/>.
    /// </summary>
    public static string? GetAvailabilityMessage()
    {
        try
        {
            GetEngine(out var error);
            return error;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Le type WinRT lui-même peut être absent de certaines installations minimales.
            return $"{MissingLanguagePackMessage} Détail technique : {ex.Message}";
        }
    }

    /// <summary>
    /// Reconnaît le texte d'un fichier image. Le résultat contient le texte complet, les lignes et
    /// leurs mots, la langue effectivement utilisée et la durée. Les erreurs sont retournées dans
    /// <see cref="OcrResult.Error"/>, en français.
    /// </summary>
    public async Task<OcrResult> RecognizeAsync(string path, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(path))
        {
            return OcrResult.Failed("Aucun chemin d'image n'a été fourni pour l'OCR.", stopwatch.Elapsed);
        }

        if (ct.IsCancellationRequested)
        {
            return OcrResult.CreateCancelled(stopwatch.Elapsed);
        }

        WinOcrEngine? engine;
        string? engineError;
        try
        {
            engine = GetEngine(out engineError);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OcrResult.Failed(
                $"Le moteur OCR de Windows n'est pas disponible : {ex.Message}",
                stopwatch.Elapsed,
                engineUnavailable: true);
        }

        if (engine is null)
        {
            return OcrResult.Failed(
                engineError ?? MissingLanguagePackMessage,
                stopwatch.Elapsed,
                engineUnavailable: true);
        }

        PreparedOcrImage? prepared = null;
        try
        {
            // Le décodage Skia et la conversion SoftwareBitmap sont synchrones : ils partent sur le
            // pool de threads pour ne jamais bloquer l'interface.
            prepared = await Task.Run(() => Prepare(path), ct).ConfigureAwait(false);
            string language = engine.RecognizerLanguage?.LanguageTag ?? "inconnu";

            WinOcrResult recognized;
            await RecognizeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                recognized = await engine.RecognizeAsync(prepared.Bitmap).AsTask(ct).ConfigureAwait(false);
            }
            finally
            {
                RecognizeGate.Release();
            }

            var lines = new List<OcrLine>(recognized.Lines.Count);
            foreach (var line in recognized.Lines)
            {
                var words = new List<OcrWord>(line.Words.Count);
                foreach (var word in line.Words)
                {
                    var rect = word.BoundingRect;
                    words.Add(new OcrWord(
                        word.Text ?? string.Empty,
                        Clamp01(rect.X / prepared.Width),
                        Clamp01(rect.Y / prepared.Height),
                        Clamp01(rect.Width / prepared.Width),
                        Clamp01(rect.Height / prepared.Height)));
                }

                string text = string.IsNullOrWhiteSpace(line.Text)
                    ? string.Join(" ", words.Select(static word => word.Text))
                    : line.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(new OcrLine(text, words));
                }
            }

            return new OcrResult(
                recognized.Text ?? string.Empty,
                lines,
                language,
                stopwatch.Elapsed,
                null);
        }
        catch (OperationCanceledException)
        {
            return OcrResult.CreateCancelled(stopwatch.Elapsed);
        }
        catch (MediaDecodeException ex)
        {
            return OcrResult.Failed($"OCR impossible pour « {path} » : {ex.Message}", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OcrResult.Failed(
                $"L'OCR a échoué pour « {path} » : {ex.Message}",
                stopwatch.Elapsed);
        }
        finally
        {
            prepared?.Dispose();
        }
    }

    private static WinOcrEngine? GetEngine(out string? error)
    {
        lock (EngineGate)
        {
            if (!_probed)
            {
                _engine = CreateEngine(out _engineError);
                _probed = true;
            }

            error = _engineError;
            return _engine;
        }
    }

    private static WinOcrEngine? CreateEngine(out string? error)
    {
        try
        {
            // 1. Les langues du profil Windows, qui correspondent le mieux aux attentes de l'utilisateur.
            var engine = WinOcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is not null)
            {
                error = null;
                return engine;
            }

            // 2. Repli explicite : l'interface et les souvenirs sont majoritairement francophones,
            //    puis viennent les deux variantes anglaises les plus courantes.
            foreach (string tag in FallbackLanguageTags)
            {
                try
                {
                    engine = WinOcrEngine.TryCreateFromLanguage(new Language(tag));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    engine = null;
                }

                if (engine is not null)
                {
                    error = null;
                    return engine;
                }
            }

            error = MissingLanguagePackMessage;
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error =
                $"Le moteur OCR de Windows n'a pas pu être initialisé : {ex.Message} " +
                "Vérifiez qu'un pack de reconnaissance optique de caractères est installé dans Windows.";
            return null;
        }
    }

    private static PreparedOcrImage Prepare(string path)
    {
        using var decoded = ImageDecoder.Decode(path);
        using var scaled = AnalysisImage.RenderOpaque(decoded.Bitmap, MaxImageDimension);
        byte[] pixels = AnalysisImage.ToBgraBytes(scaled);

        // CryptographicBuffer évite une dépendance à l'extension AsBuffer, qui n'est pas exposée
        // de la même façon selon la version de CsWinRT.
        var buffer = CryptographicBuffer.CreateFromByteArray(pixels);
        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            buffer,
            BitmapPixelFormat.Bgra8,
            scaled.Width,
            scaled.Height,
            BitmapAlphaMode.Premultiplied);
        return new PreparedOcrImage(bitmap, scaled.Width, scaled.Height);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private sealed record PreparedOcrImage(SoftwareBitmap Bitmap, int Width, int Height) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }
}
