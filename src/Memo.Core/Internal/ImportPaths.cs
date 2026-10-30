using System.Text.RegularExpressions;

namespace Memo.Core;

internal static partial class ImportPaths
{
    [GeneratedRegex("^[A-Za-z]:")]
    private static partial Regex DrivePrefix();

    public static bool TryNormalize(string? raw, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "chemin vide";
            return false;
        }

        var value = raw.Replace('\\', '/');
        if (value.IndexOf('\0') >= 0)
        {
            error = "caractère nul interdit";
            return false;
        }

        if (value.StartsWith('/'))
        {
            error = "chemin absolu interdit";
            return false;
        }

        if (DrivePrefix().IsMatch(value))
        {
            error = "lettre de lecteur interdite";
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                error = "le chemin remonte au dossier parent";
                return false;
            }

            if (segment.IndexOf(':') >= 0)
            {
                error = "deux-points interdit dans un nom d'entrée";
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            error = "chemin vide";
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    public static string Extension(string path)
    {
        var index = path.LastIndexOf('.');
        if (index < 0 || index == path.Length - 1)
        {
            return string.Empty;
        }

        return path[(index + 1)..].ToLowerInvariant();
    }

    public static bool IsInsideRoot(string rootPath, string fullPath)
    {
        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(fullPath);
        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string ToPlatformPath(string rootPath, string relativePath) =>
        Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
