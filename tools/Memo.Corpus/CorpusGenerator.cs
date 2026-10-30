using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memo.TestSupport;

namespace Memo.Corpus;

public enum CorpusFormat
{
    Zip,
    Folder,
}

public sealed record CorpusOptions(
    string OutputDirectory,
    int Seed = 42,
    int Count = 40,
    CorpusFormat Format = CorpusFormat.Zip);

public sealed record CorpusArchiveSummary(string Name, long Bytes, int Entries, string Description);

public sealed record CorpusSummary(
    string OutputDirectory,
    int Seed,
    CorpusFormat Format,
    int Memories,
    int Photos,
    int Videos,
    int Undated,
    int Overlays,
    int WebpOverlays,
    int PngOverlays,
    int OrphanOverlays,
    int DuplicateByteMemories,
    bool HasLeapDay,
    bool HasNearMidnight,
    bool HasRoleConflict,
    bool HasTraversalEntry,
    bool HasCorruptArchive,
    bool HasTwoSpaces,
    IReadOnlyList<CorpusArchiveSummary> Archives,
    IReadOnlyList<string> Cases);

/// <summary>
/// Génère un export de démonstration entièrement synthétique. Aucun média réel,
/// aucun nom de personne et aucun identifiant de compte ne sont utilisés.
/// </summary>
public static class CorpusGenerator
{
    private static readonly string[] Captions =
    {
        "BON APREM",
        "JOUR CALME",
        "FLASHBACK",
        "ROUTE DU LAC",
        "APRES LA PLUIE",
        "FIN DE JOURNEE",
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static CorpusSummary Generate(CorpusOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
        if (options.Count is < 4 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Le nombre de souvenirs doit être compris entre 4 et 500.");
        }

        var output = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(output);

        var items = BuildItems(options);
        var orphans = BuildOrphanOverlays(options);
        var allItems = items.Concat(orphans).ToArray();
        var allMemories = allItems.Select(item => item.Memory).ToArray();

        var archives = options.Format == CorpusFormat.Zip
            ? WriteZipExport(output, options, items, allMemories)
            : WriteFolderExport(output, options, allItems, allMemories);

        var undated = items.Count(item => item.Capture is null);
        var overlays = items.Count(item => item.Memory.OverlayBytes is not null) + orphans.Count;
        var webp = items.Count(item => item.OverlayIsWebp) + orphans.Count(item => item.OverlayIsWebp);
        var png = overlays - webp;
        var duplicateBytes = items.Count(item => item.IsByteDuplicate);
        var hasLeapDay = items.Any(item => item.Capture is { Month: 2, Day: 29 });
        var hasNearMidnight = items.Any(item => item.Capture is { } capture &&
                                                 (capture.Hour >= 23 || (capture.Hour == 0 && capture.Minute <= 5)));
        var hasRoleConflict = true;
        var hasTraversal = options.Format == CorpusFormat.Zip;
        var hasCorrupt = true;
        var hasTwoSpaces = true;

        var cases = new List<string>
        {
            "29 février",
            "dates proches de minuit",
            "souvenirs sans date",
            "calques orphelins",
            "UUID identiques dans deux espaces",
        };

        if (options.Format == CorpusFormat.Zip)
        {
            cases.Add("ZIP avec médias et calques séparés");
            cases.Add("conflit de rôle (deux candidats -main)");
            cases.Add("entrée ../ malveillante");
            cases.Add("archive corrompue");
        }
        else
        {
            cases.Add("arborescence extraite (médias et calques ensemble)");
            cases.Add("archive corrompue séparée dans dangers/");
        }

        if (webp > 0)
        {
            cases.Add("calques WebP sous extension .png");
        }

        var summary = new CorpusSummary(
            output,
            options.Seed,
            options.Format,
            items.Count,
            items.Count(item => !item.IsVideo),
            items.Count(item => item.IsVideo),
            undated,
            overlays,
            webp,
            png,
            orphans.Count,
            duplicateBytes,
            hasLeapDay,
            hasNearMidnight,
            hasRoleConflict,
            hasTraversal,
            hasCorrupt,
            hasTwoSpaces,
            archives,
            cases);

        WriteReadme(output, summary);
        WriteManifest(output, summary);
        return summary;
    }

    private static List<CorpusItem> BuildItems(CorpusOptions options)
    {
        var items = new List<CorpusItem>(options.Count);

        for (var index = 0; index < options.Count; index++)
        {
            var isVideo = index % 3 == 0;
            DateTimeOffset? capture = isVideo || index % 5 != 4
                ? CreateCapture(index, options.Seed)
                : null;

            var landscape = !isVideo && index % 8 == 1;
            var width = landscape ? 1920 : 1080;
            var height = landscape ? 1080 : 1920;

            var dateText = capture?.ToString("yyyy-MM-dd") ?? "DATE INCONNUE";
            var caption = Captions[index % Captions.Length];

            byte[] main;
            string mainExtension;
            var rotation = 0;
            if (isVideo)
            {
                rotation = index % 2 == 0 ? 90 : 0;
                main = SyntheticMp4.Create(capture!.Value, rotation);
                mainExtension = "mp4";
            }
            else
            {
                main = SyntheticImage.Jpeg(dateText, caption, width, height, options.Seed + index);
                mainExtension = "jpg";
            }

            var overlayWidth = isVideo
                ? (rotation == 90 ? 1080 : 1920)
                : width;
            var overlayHeight = isVideo
                ? (rotation == 90 ? 1920 : 1080)
                : height;

            byte[]? overlay = null;
            string overlayExtension = "png";
            var overlayIsWebp = false;
            var hasOverlay = index % 5 != 1;

            if (hasOverlay && index == 14)
            {
                // Calque entièrement transparent : conservé comme donnée source.
                overlay = SyntheticImage.FullyTransparentOverlay(overlayWidth, overlayHeight, SyntheticImageFormat.Png);
                overlayExtension = "png";
            }
            else if (hasOverlay)
            {
                overlayIsWebp = index % 4 != 0;
                var overlayFormat = overlayIsWebp ? SyntheticImageFormat.Webp : SyntheticImageFormat.Png;
                var scale = SyntheticImage.OverlayScales[index % SyntheticImage.OverlayScales.Count];
                overlay = SyntheticImage.Overlay(
                    overlayWidth,
                    overlayHeight,
                    scale,
                    overlayFormat,
                    "BON APREM",
                    options.Seed + index * 3);

                overlayExtension = overlayIsWebp
                    ? (index % 9 == 0 ? "webp" : "png") // WebP réel derrière une extension .png.
                    : "png";
            }

            var memory = new SyntheticMemory
            {
                Id = DeterministicGuid(options.Seed, index, "memory"),
                Kind = isVideo ? SyntheticMemoryKind.Video : SyntheticMemoryKind.Photo,
                MainBytes = main,
                MainExtension = mainExtension,
                OverlayBytes = overlay,
                OverlayExtension = overlayExtension,
                CapturedAt = capture,
                Note = "souvenir synthétique",
            };

            items.Add(new CorpusItem(memory, isVideo, capture, overlayIsWebp, overlayWidth, overlayHeight));
        }

        // Même contenu sous deux identifiants : la déduplication d'octets ne doit
        // pas fusionner les souvenirs logiques.
        var duplicates = items
            .Select((item, index) => (item, index))
            .Where(pair => !pair.item.IsVideo && pair.item.Memory.MainBytes is not null)
            .Take(2)
            .ToArray();

        if (duplicates.Length == 2)
        {
            var source = duplicates[0].item.Memory.MainBytes!;
            var target = duplicates[1];
            items[target.index] = target.item with
            {
                Memory = target.item.Memory with { MainBytes = source },
                IsByteDuplicate = true,
            };
        }

        return items;
    }

    private static List<CorpusItem> BuildOrphanOverlays(CorpusOptions options)
    {
        var count = Math.Max(2, options.Count / 20);
        var items = new List<CorpusItem>(count);

        for (var index = 0; index < count; index++)
        {
            var isWebp = index % 2 == 0;
            var format = isWebp ? SyntheticImageFormat.Webp : SyntheticImageFormat.Png;
            var extension = isWebp ? (index % 3 == 0 ? "webp" : "png") : "png";
            var scale = SyntheticImage.OverlayScales[(index + 1) % SyntheticImage.OverlayScales.Count];
            var bytes = SyntheticImage.Overlay(
                1080,
                1920,
                scale,
                format,
                "CALQUE ORPHELIN",
                options.Seed + 500 + index);

            var memory = new SyntheticMemory
            {
                Id = DeterministicGuid(options.Seed, 1000 + index, "orphan-overlay"),
                Kind = SyntheticMemoryKind.Photo,
                MainBytes = null,
                OverlayBytes = bytes,
                OverlayExtension = extension,
                CapturedAt = null,
                Note = "calque orphelin synthétique",
            };

            items.Add(new CorpusItem(memory, false, null, isWebp, 1080, 1920, IsOrphan: true));
        }

        return items;
    }

    private static IReadOnlyList<CorpusArchiveSummary> WriteZipExport(
        string output,
        CorpusOptions options,
        IReadOnlyList<CorpusItem> items,
        IReadOnlyList<SyntheticMemory> allMemories)
    {
        var mediaOne = items.Where((_, index) => index % 2 == 0).Select(item => item.Memory).ToArray();
        var mediaTwo = items.Where((_, index) => index % 2 == 1).Select(item => item.Memory).ToArray();

        var common = new ExportArchiveOptions { SpaceKey = "demo" };
        var mediaOneArchive = ExportBuilder.WriteArchive(
            Path.Combine(output, "memo-media-1.zip"),
            mediaOne,
            common with
            {
                Roles = SyntheticRoleFilter.Main,
                ZipTimestamp = new DateTimeOffset(2019, 1, 1, 12, 0, 0, TimeSpan.Zero),
                IncludeIndexHtml = true,
                IncludeJson = false,
            });

        var mediaTwoArchive = ExportBuilder.WriteArchive(
            Path.Combine(output, "memo-media-2.zip"),
            mediaTwo,
            common with
            {
                Roles = SyntheticRoleFilter.Main,
                ZipTimestamp = new DateTimeOffset(2023, 6, 1, 9, 30, 0, TimeSpan.Zero),
                IncludeIndexHtml = false,
                IncludeJson = false,
            });

        var overlayArchive = ExportBuilder.WriteArchive(
            Path.Combine(output, "memo-overlays.zip"),
            allMemories,
            common with
            {
                Roles = SyntheticRoleFilter.Overlay,
                ZipTimestamp = new DateTimeOffset(2021, 3, 1, 18, 45, 0, TimeSpan.Zero),
                IncludeIndexHtml = false,
                IncludeJson = true,
            });

        var conflictSource = items.First(item => !item.IsVideo && item.Memory.MainBytes is not null);
        var conflictMemory = conflictSource.Memory with
        {
            MainBytes = SyntheticImage.Jpeg(
                "2021-07-14",
                "CONFLIT DE ROLE",
                1080,
                1920,
                options.Seed + 9000),
            OverlayBytes = null,
        };

        var conflictArchive = ExportBuilder.WriteArchive(
            Path.Combine(output, "memo-conflit.zip"),
            new[] { conflictMemory },
            common with
            {
                Roles = SyntheticRoleFilter.Main,
                ZipTimestamp = new DateTimeOffset(2024, 1, 15, 8, 0, 0, TimeSpan.Zero),
                IncludeIndexHtml = false,
                IncludeJson = false,
                EmitDuplicateRoleConflict = true,
                IncludeTraversalEntry = true,
            });

        var otherSpaceSource = items.First(item => item.IsVideo);
        var otherSpaceMemory = otherSpaceSource.Memory with
        {
            MainBytes = SyntheticMp4.Create(new DateTimeOffset(2017, 5, 20, 12, 0, 0, TimeSpan.Zero), 0),
            OverlayBytes = null,
            CapturedAt = new DateTimeOffset(2017, 5, 20, 12, 0, 0, TimeSpan.Zero),
        };

        var otherSpaceArchive = ExportBuilder.WriteArchive(
            Path.Combine(output, "memo-autre-espace.zip"),
            new[] { otherSpaceMemory },
            common with
            {
                SpaceKey = "autre-compte",
                Roles = SyntheticRoleFilter.Main,
                ZipTimestamp = new DateTimeOffset(2022, 2, 2, 10, 0, 0, TimeSpan.Zero),
                IncludeIndexHtml = false,
                IncludeJson = false,
            });

        var corruptMemory = items[1].Memory with { OverlayBytes = null };
        var corruptArchive = ExportBuilder.WriteArchive(
            Path.Combine(output, "memo-corrompu.zip"),
            new[] { corruptMemory },
            common with
            {
                Roles = SyntheticRoleFilter.Main,
                ZipTimestamp = new DateTimeOffset(2020, 10, 10, 10, 0, 0, TimeSpan.Zero),
                IncludeIndexHtml = false,
                IncludeJson = false,
                CorruptAfterWrite = true,
            });

        return new[]
        {
            Describe("memo-media-1.zip", mediaOneArchive, "médias (partie 1, index HTML synthétique)"),
            Describe("memo-media-2.zip", mediaTwoArchive, "médias (partie 2)"),
            Describe("memo-overlays.zip", overlayArchive, "calques, calques orphelins et JSON synthétique"),
            Describe("memo-conflit.zip", conflictArchive, "conflit de rôle et entrée ../"),
            Describe("memo-autre-espace.zip", otherSpaceArchive, "même UUID dans un autre espace"),
            Describe("memo-corrompu.zip", corruptArchive, "archive tronquée"),
        };
    }

    private static IReadOnlyList<CorpusArchiveSummary> WriteFolderExport(
        string output,
        CorpusOptions options,
        IReadOnlyList<CorpusItem> allItems,
        IReadOnlyList<SyntheticMemory> allMemories)
    {
        var exportRoot = Path.Combine(output, "memo-export");
        Directory.CreateDirectory(exportRoot);

        foreach (var memory in allMemories)
        {
            WriteEntry(exportRoot, memory.MainEntryName, memory.MainBytes);
            WriteEntry(exportRoot, memory.OverlayEntryName, memory.OverlayBytes);
        }

        File.WriteAllText(
            Path.Combine(exportRoot, "index.html"),
            ExportBuilder.CreateIndexHtml(allMemories, new ExportArchiveOptions { SpaceKey = "demo" }),
            Utf8NoBom);

        var jsonPath = Path.Combine(exportRoot, "json", "memories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        File.WriteAllText(
            jsonPath,
            ExportBuilder.CreateMetadataJson(allMemories, new ExportArchiveOptions { SpaceKey = "demo" }),
            Utf8NoBom);

        var conflictSource = allItems.First(item => !item.IsVideo && item.Memory.MainBytes is not null);
        var conflictRoot = Path.Combine(output, "memo-conflit");
        WriteEntry(
            conflictRoot,
            conflictSource.Memory.MainEntryName,
            SyntheticImage.Jpeg("2021-07-14", "CONFLIT DE ROLE", 1080, 1920, options.Seed + 9000));

        var otherRoot = Path.Combine(output, "memo-autre-espace");
        var otherSpace = allItems.First(item => item.IsVideo).Memory;
        WriteEntry(
            otherRoot,
            otherSpace.MainEntryName,
            SyntheticMp4.Create(new DateTimeOffset(2017, 5, 20, 12, 0, 0, TimeSpan.Zero), 0));

        var dangerRoot = Path.Combine(output, "dangers");
        Directory.CreateDirectory(dangerRoot);
        ExportBuilder.WriteArchive(
            Path.Combine(dangerRoot, "memo-corrompu.zip"),
            new[] { allItems[1].Memory with { OverlayBytes = null } },
            new ExportArchiveOptions
            {
                Roles = SyntheticRoleFilter.Main,
                CorruptAfterWrite = true,
                IncludeIndexHtml = false,
                IncludeJson = false,
            });

        return new[]
        {
            DescribeFolder("memo-export", exportRoot, "arborescence extraite (médias et calques)"),
            DescribeFolder("memo-conflit", conflictRoot, "même UUID, média différent"),
            DescribeFolder("memo-autre-espace", otherRoot, "même UUID dans un autre espace"),
            DescribeDangerArchive(Path.Combine(dangerRoot, "memo-corrompu.zip")),
        };
    }

    private static DateTimeOffset CreateCapture(int index, int seed)
    {
        if (index == 0)
        {
            return new DateTimeOffset(2020, 2, 29, 23, 58, 30, TimeSpan.Zero);
        }

        if (index == 1)
        {
            return new DateTimeOffset(2021, 7, 14, 0, 1, 15, TimeSpan.Zero);
        }

        if (index == 2)
        {
            return new DateTimeOffset(2018, 12, 31, 23, 59, 0, TimeSpan.Zero);
        }

        if (index == 3)
        {
            return new DateTimeOffset(2019, 1, 1, 0, 0, 30, TimeSpan.Zero);
        }

        var random = new Random(unchecked(seed * 7_919 + index * 104_729));
        var year = 2018 + (int)(unchecked((uint)(index + seed)) % 6u);
        var month = 1 + random.Next(12);
        var day = 1 + random.Next(DateTime.DaysInMonth(year, month));

        int hour;
        int minute;
        if (index % 6 == 5)
        {
            hour = index % 2 == 0 ? 23 : 0;
            minute = index % 2 == 0 ? 55 + random.Next(5) : random.Next(5);
        }
        else
        {
            hour = 6 + random.Next(17);
            minute = random.Next(60);
        }

        return new DateTimeOffset(year, month, day, hour, minute, random.Next(60), TimeSpan.Zero);
    }

    private static Guid DeterministicGuid(int seed, int index, string salt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"memo-corpus:{seed}:{salt}:{index}"));
        var guidBytes = bytes[..16];
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x40);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private static void WriteEntry(string root, string? entryName, byte[]? content)
    {
        if (entryName is null || content is null)
        {
            return;
        }

        var path = Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, content);
    }

    private static CorpusArchiveSummary Describe(string name, ExportArchiveInfo archive, string description)
    {
        var info = new FileInfo(archive.Path);
        return new CorpusArchiveSummary(name, info.Length, archive.EntryNames.Count, description);
    }

    private static CorpusArchiveSummary DescribeFolder(string name, string path, string description)
    {
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();
        var bytes = files.Sum(file => new FileInfo(file).Length);
        return new CorpusArchiveSummary(name, bytes, files.Length, description);
    }

    private static CorpusArchiveSummary DescribeDangerArchive(string path)
    {
        var info = new FileInfo(path);
        return new CorpusArchiveSummary(Path.Combine("dangers", Path.GetFileName(path)), info.Length, 1, "archive tronquée");
    }

    private static void WriteReadme(string output, CorpusSummary summary)
    {
        var text = $"""
            Corpus synthétique Memo
            =======================

            Graine       : {summary.Seed}
            Format       : {summary.Format}
            Souvenirs    : {summary.Memories} (photos : {summary.Photos}, vidéos : {summary.Videos})
            Sans date    : {summary.Undated}
            Calques      : {summary.Overlays} (WebP : {summary.WebpOverlays}, PNG : {summary.PngOverlays})
            Orphelins    : {summary.OrphanOverlays}

            Contenu généré par tools/Memo.Corpus. Aucune donnée réelle, aucun nom
            de personne, aucun identifiant de compte et aucun média personnel.
            Les fichiers .zip ne sont pas versionnés ; régénérez ce dossier à la demande.

            Cas couverts : {string.Join(", ", summary.Cases)}.
            """;

        File.WriteAllText(Path.Combine(output, "LISEZMOI.txt"), text, Utf8NoBom);
    }

    private static void WriteManifest(string output, CorpusSummary summary)
    {
        // Le chemin absolu de sortie est volontairement neutralisé : le manifeste
        // doit rester identique pour une même graine, quel que soit le dossier.
        var manifest = summary with { OutputDirectory = "." };
        var json = JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            });

        File.WriteAllText(Path.Combine(output, "corpus-manifest.json"), json, Utf8NoBom);
    }

    private sealed record CorpusItem(
        SyntheticMemory Memory,
        bool IsVideo,
        DateTimeOffset? Capture,
        bool OverlayIsWebp,
        int OverlayWidth,
        int OverlayHeight,
        bool IsOrphan = false,
        bool IsByteDuplicate = false);
}
