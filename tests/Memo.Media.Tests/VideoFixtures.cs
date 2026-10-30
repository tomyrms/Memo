using System.ComponentModel;
using System.Diagnostics;

namespace Memo.Media.Tests;

internal static class Ffmpeg
{
    private static readonly Lazy<string?> Executable = new(Find, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string?> EncoderList = new(ReadEncoders, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Require()
    {
        string? path = Executable.Value;
        if (path is null)
        {
            Assert.Inconclusive("ffmpeg est absent de cette machine ; le test vidéo est ignoré.");
        }

        return path!;
    }

    public static bool HasEncoder(string name)
    {
        if (Executable.Value is null)
        {
            return false;
        }

        string? encoders = EncoderList.Value;
        return encoders is not null &&
               encoders.Contains($" {name} ", StringComparison.OrdinalIgnoreCase);
    }

    public static string Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Require())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg n'a pas pu être démarré.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(60_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new InvalidOperationException("ffmpeg n'a pas terminé dans le délai de 60 secondes.");
        }

        string output = standardOutput.GetAwaiter().GetResult();
        string error = standardError.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg a échoué (code {process.ExitCode}).{Environment.NewLine}{error}{Environment.NewLine}{output}");
        }

        return output;
    }

    private static string? Find()
    {
        try
        {
            var startInfo = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-version");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            return process.ExitCode == 0 ? "ffmpeg" : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadEncoders()
    {
        try
        {
            return Run("-hide_banner", "-encoders");
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

internal static class VideoFixtures
{
    private static readonly Lazy<string> H264 = new(
        () => CreateH264(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> Hevc = new(
        () => CreateHevc(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string H264Path => H264.Value;

    public static string HevcPath => Hevc.Value;

    private static string CreateH264()
    {
        Ffmpeg.Require();
        if (!Ffmpeg.HasEncoder("libx264"))
        {
            Assert.Inconclusive("L'encodeur libx264 est absent de ce build ffmpeg ; le test H.264 est ignoré.");
        }

        string directory = TestWorkspace.SharedDirectory("video-assets");
        string path = Path.Combine(directory, "red-320x240-h264.mp4");
        if (File.Exists(path))
        {
            return path;
        }

        Ffmpeg.Run(
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-f", "lavfi",
            "-i", "color=c=red:s=320x240:d=1:r=10",
            "-an",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            path);

        return path;
    }

    private static string CreateHevc()
    {
        Ffmpeg.Require();
        if (!Ffmpeg.HasEncoder("libx265"))
        {
            Assert.Inconclusive("L'encodeur libx265 est absent de ce build ffmpeg ; le test HEVC est ignoré.");
        }

        string directory = TestWorkspace.SharedDirectory("video-assets");
        string path = Path.Combine(directory, "blue-320x240-hevc.mp4");
        if (File.Exists(path))
        {
            return path;
        }

        Ffmpeg.Run(
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-f", "lavfi",
            "-i", "color=c=blue:s=320x240:d=1:r=10",
            "-an",
            "-c:v", "libx265",
            "-preset", "ultrafast",
            "-pix_fmt", "yuv420p",
            "-tag:v", "hvc1",
            "-x265-params", "log-level=error",
            path);

        return path;
    }
}
