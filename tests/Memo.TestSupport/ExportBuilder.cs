using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Memo.TestSupport;

/// <summary>Type de média d'un souvenir synthétique.</summary>
public enum SyntheticMemoryKind
{
    Photo,
    Video,
}

/// <summary>
/// Souvenir synthétique destiné à être écrit dans un export. Les octets sont
/// fournis par l'appelant afin de pouvoir réutiliser exactement les mêmes
/// données sous deux identifiants (doublon d'octets).
/// </summary>
public sealed record SyntheticMemory
{
    public required Guid Id { get; init; }
    public SyntheticMemoryKind Kind { get; init; } = SyntheticMemoryKind.Photo;
    public byte[]? MainBytes { get; init; }
    public string MainExtension { get; init; } = "jpg";
    public byte[]? OverlayBytes { get; init; }
    public string OverlayExtension { get; init; } = "png";
    public DateTimeOffset? CapturedAt { get; init; }
    public string? Note { get; init; }

    public string? MainEntryName => MainBytes is null ? null : $"memories/{Id:D}-main.{NormalizeExtension(MainExtension)}";

    public string? OverlayEntryName => OverlayBytes is null ? null : $"memories/{Id:D}-overlay.{NormalizeExtension(OverlayExtension)}";

    internal static string NormalizeExtension(string extension) =>
        extension.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "jpeg" => "jpg",
            var value when value.Length > 0 => value,
            _ => throw new ArgumentException("Extension vide.", nameof(extension)),
        };
}

/// <summary>Entrée brute ajoutée à une archive synthétique.</summary>
public sealed record ZipEntrySpec(string Name, byte[] Content, DateTimeOffset? Timestamp = null);

/// <summary>Rôles écrits dans une archive synthétique.</summary>
[Flags]
public enum SyntheticRoleFilter
{
    None = 0,
    Main = 1,
    Overlay = 2,
    Both = Main | Overlay,
}

/// <summary>Options d'écriture d'une archive synthétique.</summary>
public sealed record ExportArchiveOptions
{
    /// <summary>Espace source logique. Deux archives de même UUID mais d'espaces différents restent distinctes.</summary>
    public string SpaceKey { get; init; } = "synthetic";

    /// <summary>Rôles à écrire. Les autres rôles restent absents de l'archive.</summary>
    public SyntheticRoleFilter Roles { get; init; } = SyntheticRoleFilter.Both;

    /// <summary>Date commune aux entrées de l'archive (les ZIP n'ont pas d'horodatage global).</summary>
    public DateTimeOffset ZipTimestamp { get; init; } = new(2021, 7, 14, 12, 0, 0, TimeSpan.Zero);

    public bool IncludeIndexHtml { get; init; } = true;

    public bool IncludeJson { get; init; } = true;

    /// <summary>Ajoute une entrée dont le nom sort de l'archive (test de sécurité).</summary>
    public bool IncludeTraversalEntry { get; init; }

    public string TraversalEntryName { get; init; } = "../hors-export.jpg";

    /// <summary>Ajoute une seconde entrée de rôle principal pour le même UUID (conflit de rôle).</summary>
    public bool EmitDuplicateRoleConflict { get; init; }

    /// <summary>UUID utilisé pour le conflit de rôle ; le premier média principal par défaut.</summary>
    public Guid? RoleConflictId { get; init; }

    /// <summary>Entrées supplémentaires, par exemple pour tester un nom inattendu ou un autre format.</summary>
    public IReadOnlyList<ZipEntrySpec> ExtraEntries { get; init; } = Array.Empty<ZipEntrySpec>();

    /// <summary>Tronque l'archive après écriture pour simuler un téléchargement interrompu.</summary>
    public bool CorruptAfterWrite { get; init; }

    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest;
}

/// <summary>Description d'une archive synthétique écrite sur disque.</summary>
public sealed record ExportArchiveInfo(
    string Path,
    string SpaceKey,
    IReadOnlyList<string> EntryNames,
    IReadOnlyList<Guid> MemoryIds,
    bool Corrupted);

/// <summary>Description d'un export dont les médias et les calques sont répartis.</summary>
public sealed record SplitExportInfo(
    IReadOnlyList<ExportArchiveInfo> MediaArchives,
    ExportArchiveInfo OverlayArchive,
    IReadOnlyList<Guid> MemoryIds);

/// <summary>Entrée lue dans un ZIP, sans extraction.</summary>
public sealed record ZipEntryInfo(string Name, long Length, long CompressedLength);

/// <summary>
/// Construit des archives ZIP synthétiques au format observé
/// <c>memories/&lt;uuid&gt;-main.jpg|mp4</c> et <c>-overlay.png</c>, avec un index
/// HTML et des métadonnées JSON explicitement marquées synthétiques.
/// </summary>
public static class ExportBuilder
{
    private static readonly DateTimeOffset ZipMin = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ZipMax = new(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Écrit une archive et retourne sa description. Le chemin est créé au besoin.</summary>
    public static ExportArchiveInfo WriteArchive(
        string path,
        IEnumerable<SyntheticMemory> memories,
        ExportArchiveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(memories);

        options ??= new ExportArchiveOptions();
        var ordered = memories.OrderBy(memory => memory.Id).ToArray();
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entryNames = new List<string>();

        try
        {
            using (var archive = ZipFile.Open(fullPath, ZipArchiveMode.Create, Encoding.UTF8))
            {
                foreach (var memory in ordered)
                {
                    if (options.Roles.HasFlag(SyntheticRoleFilter.Main))
                    {
                        AddEntry(archive, memory.MainEntryName, memory.MainBytes, options, entryNames);
                    }

                    if (options.Roles.HasFlag(SyntheticRoleFilter.Overlay))
                    {
                        AddEntry(archive, memory.OverlayEntryName, memory.OverlayBytes, options, entryNames);
                    }
                }

                if (options.EmitDuplicateRoleConflict && options.Roles.HasFlag(SyntheticRoleFilter.Main))
                {
                    var conflict = options.RoleConflictId is { } id
                        ? ordered.FirstOrDefault(memory => memory.Id == id)
                        : ordered.FirstOrDefault(memory => memory.MainBytes is not null);

                    if (conflict?.MainEntryName is { } conflictName && conflict.MainBytes is { } conflictBytes)
                    {
                        // Même nom, octets différents : l'importateur doit conserver les deux candidats.
                        AddEntry(archive, conflictName, Mutate(conflictBytes, 97), options, entryNames, allowDuplicateName: true);
                    }
                }

                if (options.IncludeIndexHtml)
                {
                    var html = CreateIndexHtml(ordered, options);
                    AddEntry(archive, "index.html", Encoding.UTF8.GetBytes(html), options, entryNames);
                }

                if (options.IncludeJson)
                {
                    var json = CreateMetadataJson(ordered, options);
                    AddEntry(archive, "json/memories.json", Encoding.UTF8.GetBytes(json), options, entryNames);
                }

                if (options.IncludeTraversalEntry)
                {
                    var bytes = SyntheticImage.Jpeg("1999-01-01", "HORS EXPORT", 320, 240, seed: 404);
                    AddEntry(archive, options.TraversalEntryName, bytes, options, entryNames);
                }

                foreach (var extra in options.ExtraEntries)
                {
                    AddEntry(archive, extra.Name, extra.Content, options, entryNames, extra.Timestamp, allowDuplicateName: true);
                }
            }

            if (options.CorruptAfterWrite)
            {
                CorruptArchive(fullPath);
            }
        }
        catch
        {
            TryDelete(fullPath);
            throw;
        }

        return new ExportArchiveInfo(
            fullPath,
            options.SpaceKey,
            entryNames,
            ordered.Select(memory => memory.Id).ToArray(),
            options.CorruptAfterWrite);
    }

    /// <summary>Écrit une archive et retourne uniquement son chemin.</summary>
    public static string Write(string path, IEnumerable<SyntheticMemory> memories, ExportArchiveOptions? options = null) =>
        WriteArchive(path, memories, options).Path;

    /// <summary>
    /// Écrit plusieurs archives en séparant médias et calques. Les médias sont
    /// répartis sur <paramref name="mediaParts"/> archives, les calques vont dans
    /// une archive distincte. L'index reste dans la première archive média et les
    /// métadonnées JSON dans l'archive de calques.
    /// </summary>
    public static SplitExportInfo WriteMediaOverlaySplit(
        string directory,
        string baseName,
        IReadOnlyList<SyntheticMemory> memories,
        int mediaParts = 2,
        ExportArchiveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaParts);

        options ??= new ExportArchiveOptions();
        Directory.CreateDirectory(directory);

        var ordered = memories.OrderBy(memory => memory.Id).ToArray();
        var parts = Enumerable.Range(0, mediaParts)
            .Select(_ => new List<SyntheticMemory>())
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            parts[index % mediaParts].Add(ordered[index]);
        }

        var mediaArchives = new List<ExportArchiveInfo>(mediaParts);
        for (var index = 0; index < parts.Length; index++)
        {
            var mediaOptions = options with
            {
                Roles = SyntheticRoleFilter.Main,
                IncludeIndexHtml = index == 0,
                IncludeJson = false,
                EmitDuplicateRoleConflict = false,
                IncludeTraversalEntry = false,
                CorruptAfterWrite = false,
            };

            var archive = WriteArchive(
                System.IO.Path.Combine(directory, $"{baseName}-media-{index + 1}.zip"),
                parts[index],
                mediaOptions);
            mediaArchives.Add(archive);
        }

        var overlayOptions = options with
        {
            Roles = SyntheticRoleFilter.Overlay,
            IncludeIndexHtml = false,
            IncludeJson = true,
            EmitDuplicateRoleConflict = false,
            IncludeTraversalEntry = false,
            CorruptAfterWrite = false,
        };

        var overlays = WriteArchive(
            System.IO.Path.Combine(directory, $"{baseName}-overlays.zip"),
            ordered,
            overlayOptions);

        return new SplitExportInfo(mediaArchives, overlays, ordered.Select(memory => memory.Id).ToArray());
    }

    /// <summary>Tronque une archive pour rendre sa table centrale illisible.</summary>
    public static void CorruptArchive(string path, double keepRatio = 0.6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keepRatio, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(keepRatio, 1);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        if (stream.Length == 0)
        {
            return;
        }

        stream.SetLength(Math.Max(1, (long)(stream.Length * keepRatio)));
    }

    /// <summary>Liste les entrées d'une archive sans extraire son contenu.</summary>
    public static IReadOnlyList<ZipEntryInfo> ListEntries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries
            .Select(entry => new ZipEntryInfo(entry.FullName, entry.Length, entry.CompressedLength))
            .ToArray();
    }

    /// <summary>Lit le contenu d'une entrée, pour les assertions de test.</summary>
    public static byte[] ReadEntry(string path, string entryName)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(entryName)
                    ?? throw new FileNotFoundException($"Entrée absente : {entryName}", entryName);
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void AddEntry(
        ZipArchive archive,
        string? name,
        byte[]? content,
        ExportArchiveOptions options,
        List<string> entryNames,
        DateTimeOffset? timestamp = null,
        bool allowDuplicateName = false)
    {
        if (name is null || content is null)
        {
            return;
        }

        if (!allowDuplicateName && entryNames.Contains(name, StringComparer.Ordinal))
        {
            return;
        }

        var entry = archive.CreateEntry(name, options.CompressionLevel);
        entry.LastWriteTime = ClampZipTimestamp(timestamp ?? options.ZipTimestamp);

        using (var stream = entry.Open())
        {
            stream.Write(content);
        }

        entryNames.Add(name);
    }

    private static DateTimeOffset ClampZipTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        if (utc < ZipMin)
        {
            return ZipMin;
        }

        return utc > ZipMax ? ZipMax : utc;
    }

    private static byte[] Mutate(byte[] content, byte delta)
    {
        // Ajoute un octet final plutôt que de casser l'en-tête : les deux
        // candidats restent des fichiers plausibles, avec des octets distincts.
        var copy = new byte[content.Length + 1];
        content.CopyTo(copy, 0);
        copy[^1] = delta;
        return copy;
    }

    /// <summary>Construit l'index HTML statique d'une archive synthétique, sans script ni lien distant.</summary>
    public static string CreateIndexHtml(IReadOnlyList<SyntheticMemory> memories, ExportArchiveOptions? options = null)
    {
        options ??= new ExportArchiveOptions();
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"fr\">");
        builder.AppendLine("<head><meta charset=\"utf-8\"><title>Export synthétique Memo</title></head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<h1>Export synthétique Memo</h1>");
        builder.AppendLine("<p>Contenu généré pour les tests. Aucune donnée personnelle, aucun script, aucun lien distant.</p>");
        builder.AppendLine($"<p>Espace source : {WebUtility.HtmlEncode(options.SpaceKey)}</p>");
        builder.AppendLine("<ul>");
        foreach (var memory in memories)
        {
            if (options.Roles.HasFlag(SyntheticRoleFilter.Main) && memory.MainEntryName is { } main)
            {
                builder.AppendLine(
                    $"<li data-memory=\"{memory.Id:D}\">{WebUtility.HtmlEncode(main)}</li>");
            }

            if (options.Roles.HasFlag(SyntheticRoleFilter.Overlay) && memory.OverlayEntryName is { } overlay)
            {
                builder.AppendLine(
                    $"<li data-memory=\"{memory.Id:D}\" data-role=\"overlay\">{WebUtility.HtmlEncode(overlay)}</li>");
            }
        }

        builder.AppendLine("</ul>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    /// <summary>Construit les métadonnées JSON synthétiques, sans reproduire un index Snapchat réel.</summary>
    public static string CreateMetadataJson(IReadOnlyList<SyntheticMemory> memories, ExportArchiveOptions? options = null)
    {
        options ??= new ExportArchiveOptions();
        var document = new SyntheticJsonDocument(
            Synthetic: true,
            FormatNote: "Métadonnées inventées pour les tests ; aucun index Snapchat réel n'est reproduit.",
            Space: options.SpaceKey,
            GeneratedFor: options.ZipTimestamp,
            Memories: memories.Select(memory => new SyntheticJsonMemory(
                Id: memory.Id.ToString("D"),
                Kind: memory.Kind == SyntheticMemoryKind.Video ? "video" : "photo",
                RoleMain: memory.MainEntryName,
                RoleOverlay: memory.OverlayEntryName,
                CapturedAtUtc: memory.CapturedAt?.ToUniversalTime(),
                Note: memory.Note)).ToArray());

        return JsonSerializer.Serialize(document, JsonOptions);
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
            // Le fichier partiel sera ignoré par l'appelant.
        }
    }

    private sealed record SyntheticJsonDocument(
        bool Synthetic,
        string FormatNote,
        string Space,
        DateTimeOffset GeneratedFor,
        IReadOnlyList<SyntheticJsonMemory> Memories);

    private sealed record SyntheticJsonMemory(
        string Id,
        string Kind,
        string? RoleMain,
        string? RoleOverlay,
        DateTimeOffset? CapturedAtUtc,
        string? Note);
}
