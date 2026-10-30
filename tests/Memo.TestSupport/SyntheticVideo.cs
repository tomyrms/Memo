using System.Diagnostics;

namespace Memo.TestSupport;

/// <summary>
/// Génère un MP4 réellement décodable avec ffmpeg lorsque l'outil est disponible.
/// Le MP4 minimal de <see cref="SyntheticMp4"/> reste la référence déterministe :
/// ce helper est facultatif et ne fait jamais échouer la génération du corpus.
/// </summary>
public static class SyntheticVideo
{
    private static readonly Lazy<string?> DetectedFfmpeg = new(FindFfmpegCore);

    /// <summary>Chemin de ffmpeg trouvé sur le PATH, ou null.</summary>
    public static string? FfmpegPath => DetectedFfmpeg.Value;

    /// <summary>Indique si la génération d'une vidéo réellement lisible est possible.</summary>
    public static bool IsAvailable => FfmpegPath is not null;

    /// <summary>
    /// Crée un MP4 H.264 avec ffmpeg. Retourne null et un diagnostic lorsque
    /// ffmpeg est absent ou que l'encodage échoue. La rotation 90 applique un
    /// transpose (l'encodage devient portrait), ce qui diffère du cas
    /// « paysage encodé + matrice de rotation » couvert par <see cref="SyntheticMp4"/>.
    /// </summary>
    public static string? RealMp4(
        string destinationPath,
        DateTimeOffset creationTimeUtc,
        out string? diagnostic,
        int rotation = 0,
        int width = 640,
        int height = 360,
        double durationSeconds = 2)
    {
        diagnostic = null;
        var ffmpeg = FfmpegPath;
        if (ffmpeg is null)
        {
            diagnostic = "ffmpeg introuvable sur le PATH ; vidéo réelle ignorée.";
            return null;
        }

        if (rotation is not (0 or 90))
        {
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Seules les rotations 0 et 90 sont générées.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationSeconds);

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var timestamp = creationTimeUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'");
        var source = $"testsrc2=size={width}x{height}:rate=15:duration={durationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-y",
            "-f", "lavfi",
            "-i", source,
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            "-map_metadata", "-1",
            "-metadata", $"creation_time={timestamp}",
        };

        if (rotation == 90)
        {
            arguments.AddRange(new[] { "-vf", "transpose=1" });
        }

        arguments.Add(destinationPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                diagnostic = "ffmpeg n'a pas démarré.";
                return null;
            }

            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Le processus s'est arrêté entre le test et le kill.
                }

                try
                {
                    process.WaitForExit(5_000);
                    _ = errorTask.GetAwaiter().GetResult();
                    _ = outputTask.GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Les flux sont déjà interrompus ; seul le diagnostic compte.
                }

                diagnostic = "ffmpeg a dépassé 60 secondes.";
                return null;
            }

            process.WaitForExit();
            var error = errorTask.GetAwaiter().GetResult();
            var output = outputTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0 || !File.Exists(destinationPath) || new FileInfo(destinationPath).Length == 0)
            {
                diagnostic = $"ffmpeg a échoué (code {process.ExitCode}) : {Trim(error + output)}";
                TryDelete(destinationPath);
                return null;
            }

            return destinationPath;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostic = $"Exécution de ffmpeg impossible : {exception.Message}";
            TryDelete(destinationPath);
            return null;
        }
    }

    private static string? FindFfmpegCore()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("MEMO_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Entrée PATH invalide : ignorée.
                }
            }
        }

        return null;
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 400 ? value : value[..400] + "…";
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
            // Un fichier partiel restant ne doit pas masquer le diagnostic ffmpeg.
        }
    }
}
