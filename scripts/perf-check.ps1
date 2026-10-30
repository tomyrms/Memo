#requires -Version 7.0
<#
.SYNOPSIS
    Mesure reproductible d'une bibliothèque Memo volumineuse, sans modifier le dépôt.

.DESCRIPTION
    Génère un corpus synthétique avec tools/Memo.Corpus (par lots de 500, limite de l'outil),
    crée une bibliothèque temporaire, importe les archives, puis exécute un harnais hors dépôt
    qui référence Memo.Core et Memo.Media. Le harnais mesure l'import, les requêtes, les
    miniatures, la sauvegarde/restauration, la mémoire, les handles, les verrous SQLite et,
    en option, la montée en charge du catalogue par duplication de lignes.

    Aucun média réel n'est utilisé et aucun accès réseau n'est effectué.

.PARAMETER Count
    Nombre de souvenirs à générer, entre 4 et 2000. Au-delà de 500, plusieurs lots avec des
    graines distinctes sont produits (limite imposée par tools/Memo.Corpus).

.PARAMETER Root
    Dossier de travail temporaire. Il est supprimé à la fin, sauf avec -Keep.
    Il ne peut pas contenir le dépôt.

.PARAMETER Seed
    Graine du premier lot. Les lots suivants utilisent Seed+1, Seed+2, etc.

.PARAMETER Photos
    Nombre de miniatures photo à mesurer (200 par défaut).

.PARAMETER Iterations
    Nombre de passes mesurées par requête, après une passe à blanc (5 par défaut).

.PARAMETER CatalogScale
    Facteur de duplication du catalogue pour le test de montée en charge (8 par défaut,
    0 ou 1 pour désactiver). Les médias ne sont pas dupliqués : seules les lignes SQLite
    sont copiées, ce qui isole le coût des requêtes.

.PARAMETER Keep
    Conserve le corpus, la bibliothèque, le harnais et results.json pour inspection.

.EXAMPLE
    pwsh -File scripts/perf-check.ps1 -Count 1200 -Root "$env:TEMP\memo-perf"

.EXAMPLE
    pwsh -File scripts/perf-check.ps1 -Count 300 -Root "$env:TEMP\memo-perf-300" -CatalogScale 0 -Keep
#>
[CmdletBinding()]
param(
    [ValidateRange(4, 2000)]
    [int]$Count = 1200,

    [string]$Root = (Join-Path $env:TEMP 'memo-perf'),

    [ValidateRange(1, 1000000)]
    [int]$Seed = 42,

    [ValidateRange(0, 5000)]
    [int]$Photos = 200,

    [ValidateRange(1, 20)]
    [int]$Iterations = 5,

    [ValidateRange(0, 64)]
    [int]$CatalogScale = 8,

    [switch]$Keep
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
}
catch {
    # Sortie redirigée : l'encodage par défaut suffit.
}

$repo = Split-Path -Parent $PSScriptRoot
$coreProject = Join-Path $repo 'src\Memo.Core\Memo.Core.csproj'
$mediaProject = Join-Path $repo 'src\Memo.Media\Memo.Media.csproj'
$corpusProject = Join-Path $repo 'tools\Memo.Corpus\Memo.Corpus.csproj'

foreach ($required in @($coreProject, $mediaProject, $corpusProject)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Projet introuvable : $required. Le script doit rester dans <dépôt>/scripts/."
    }
}

$rootFull = [System.IO.Path]::GetFullPath($Root)
$repoFull = [System.IO.Path]::GetFullPath($repo)
$separator = [System.IO.Path]::DirectorySeparatorChar
if ($rootFull -eq $repoFull -or
    $repoFull.StartsWith($rootFull.TrimEnd([char[]]@($separator)) + $separator, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "-Root ne peut pas être le dépôt ni un de ses parents : $rootFull"
}

if ([System.IO.Path]::GetPathRoot($rootFull) -eq $rootFull) {
    throw "-Root ne peut pas être la racine d'un volume : $rootFull"
}

$corpusRoot = Join-Path $rootFull 'corpus'
$libraryRoot = Join-Path $rootFull 'library'
$harnessRoot = Join-Path $rootFull 'harness'
$thumbsRoot = Join-Path $rootFull 'thumbs'
$backupPath = Join-Path $rootFull 'backup.zip'
$restoreRoot = Join-Path $rootFull 'restored'
$resultsPath = Join-Path $rootFull 'results.json'

$completed = $false
$startedAt = Get-Date

try {
    if (Test-Path -LiteralPath $rootFull) {
        Write-Host "Suppression de l'ancien dossier de travail : $rootFull"
        Remove-Item -LiteralPath $rootFull -Recurse -Force
    }

    New-Item -ItemType Directory -Path $corpusRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $harnessRoot -Force | Out-Null

    Write-Host ''
    Write-Host '=== 1/3 Génération du corpus synthétique ===' -ForegroundColor Cyan
    & dotnet build $corpusProject -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Échec de la compilation de tools/Memo.Corpus (code $LASTEXITCODE)."
    }

    $remaining = $Count
    $batchSeed = $Seed
    $batchIndex = 0
    while ($remaining -gt 0) {
        $batchCount = [Math]::Min(500, $remaining)
        $batchDirectory = Join-Path $corpusRoot ('seed-{0}' -f $batchSeed)
        $batchIndex++
        Write-Host ("  Lot {0} : {1} souvenirs, graine {2}" -f $batchIndex, $batchCount, $batchSeed)
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        & dotnet run --no-build --project $corpusProject -c Release -- `
            --out $batchDirectory --seed $batchSeed --count $batchCount --format zip
        if ($LASTEXITCODE -ne 0) {
            throw "Échec de la génération du lot $batchIndex (code $LASTEXITCODE)."
        }

        $watch.Stop()
        Write-Host ("    terminé en {0:0.0} s" -f $watch.Elapsed.TotalSeconds)
        $remaining -= $batchCount
        $batchSeed++
    }

    $corpusZips = @(Get-ChildItem -LiteralPath $corpusRoot -Recurse -File -Filter '*.zip')
    $corpusBytes = ($corpusZips | Measure-Object -Property Length -Sum).Sum
    Write-Host ("  Corpus : {0} archives, {1:0.0} Mio" -f $corpusZips.Count, ($corpusBytes / 1MB))

    Write-Host ''
    Write-Host '=== 2/3 Compilation du harnais hors dépôt ===' -ForegroundColor Cyan
    $coreXml = [System.Security.SecurityElement]::Escape($coreProject)
    $mediaXml = [System.Security.SecurityElement]::Escape($mediaProject)
    $projectFile = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AssemblyName>MemoPerf</AssemblyName>
    <RootNamespace>MemoPerf</RootNamespace>
    <ServerGarbageCollection>false</ServerGarbageCollection>
    <InvariantGlobalization>false</InvariantGlobalization>
    <NoWarn>$(NoWarn);CS8602</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="__CORE_PROJECT__" />
    <ProjectReference Include="__MEDIA_PROJECT__" />
  </ItemGroup>
</Project>
'@
    $projectFile = $projectFile.Replace('__CORE_PROJECT__', $coreXml).Replace('__MEDIA_PROJECT__', $mediaXml)
    [System.IO.File]::WriteAllText(
        (Join-Path $harnessRoot 'MemoPerf.csproj'),
        $projectFile,
        [System.Text.UTF8Encoding]::new($false))

    $program = @'
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Memo.Core;
using Memo.Media;
using Microsoft.Data.Sqlite;

namespace MemoPerf;

internal static class Program
{
    private static readonly List<Metric> Metrics = new();
    private static readonly Dictionary<string, object?> EnvironmentInfo = new();
    private static readonly List<string> Notes = new();
    private static readonly Process Proc = Process.GetCurrentProcess();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Metric(string Section, string Name, double Value, string Unit, string Detail);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
        }

        Dictionary<string, string> options;
        try
        {
            options = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }

        if (options.ContainsKey("--help"))
        {
            PrintUsage();
            return 0;
        }

        var libraryRoot = Require(options, "--library");
        var outputPath = Require(options, "--out");
        var iterations = int.Parse(options.GetValueOrDefault("--iterations", "5"), Inv);
        var catalogScale = int.Parse(options.GetValueOrDefault("--catalog-scale", "0"), Inv);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        // Mode « montée en charge seule » : réutilise une bibliothèque déjà importée.
        if (options.TryGetValue("--scale-library", out var scaleLibrary))
        {
            CaptureEnvironment(scaleLibrary, Array.Empty<string>());
            await RunCatalogScaleAsync(scaleLibrary, Math.Max(2, catalogScale), iterations).ConfigureAwait(false);
            WriteReport(outputPath, scaleLibrary, 0, 0);
            PrintTable();
            return 0;
        }

        var corpusRoots = Require(options, "--corpus")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var thumbsRoot = options.GetValueOrDefault("--thumbs", Path.Combine(Path.GetDirectoryName(libraryRoot)!, "thumbs"));
        var backupPath = options.GetValueOrDefault("--backup", Path.Combine(Path.GetDirectoryName(libraryRoot)!, "backup.zip"));
        var restoreRoot = options.GetValueOrDefault("--restore", Path.Combine(Path.GetDirectoryName(libraryRoot)!, "restored"));
        var photoTarget = int.Parse(options.GetValueOrDefault("--photos", "200"), Inv);

        var corpusZips = corpusRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (corpusZips.Count == 0)
        {
            Console.Error.WriteLine("Aucune archive ZIP trouvée dans le corpus.");
            return 2;
        }

        CaptureEnvironment(libraryRoot, corpusZips);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var started = Stopwatch.StartNew();
        MemoLibrary? library = null;
        try
        {
            // Création de la bibliothèque.
            DeleteDirectory(libraryRoot);
            Directory.CreateDirectory(libraryRoot);
            var createMs = await TimeOnceAsync(
                "Bibliothèque",
                "Création (CreateAsync)",
                async () => library = await MemoLibrary.CreateAsync(
                    libraryRoot,
                    "Perf corpus synthétique",
                    TimeZoneInfo.Local.Id)).ConfigureAwait(false);

            var space = await library!.Catalog.CreateSpaceAsync("perf").ConfigureAwait(false);
            Add("Bibliothèque", "Espace créé", space.Id, "", space.Name);

            // Import.
            var sampler = new ImportSampler(Path.Combine(libraryRoot, "catalog.db"));
            var wsBefore = Snapshot();
            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var corpusBytes = corpusZips.Sum(path => new FileInfo(path).Length);
            var importWatch = Stopwatch.StartNew();
            var importResult = await library.Import
                .RunAsync(new ImportRequest(space.Id, corpusZips, false, "perf-check"), sampler)
                .ConfigureAwait(false);
            importWatch.Stop();
            var wsAfter = Snapshot();
            var allocAfter = GC.GetTotalAllocatedBytes(precise: true);

            var counters = importResult.Counters;
            var seconds = importWatch.Elapsed.TotalSeconds;
            var mediaFiles = counters.MediaImported + counters.OverlaysImported;
            Add("Import", "Durée totale", importWatch.Elapsed.TotalMilliseconds, "ms",
                $"{seconds:0.00} s ; {corpusZips.Count} archives ; {counters.EntriesSeen} entrées");
            Add("Import", "Débit entrées", counters.EntriesSeen / seconds, "entrées/s",
                $"{counters.EntriesSeen} entrées examinées");
            Add("Import", "Débit médias", mediaFiles / seconds, "fichiers/s",
                $"{counters.MediaImported} médias + {counters.OverlaysImported} calques");
            Add("Import", "Débit octets", corpusBytes / seconds / (1024.0 * 1024.0), "Mio/s",
                $"{corpusBytes / (1024.0 * 1024.0):0.0} Mio d'archives lues");
            Add("Import", "Souvenirs créés", counters.MemoriesCreated, "", $"{counters.MemoriesUpdated} mis à jour");
            Add("Import", "Doublons ignorés", counters.DuplicatesSkipped, "", $"{counters.Unrecognized} non reconnus, {counters.Errors} erreurs");
            Add("Import", "Diagnostics", importResult.Diagnostics.Count, "",
                string.Join(", ", importResult.Diagnostics.GroupBy(d => d.Severity).Select(g => $"{g.Key}={g.Count()}")));
            Add("Import", "Mémoire de travail avant", wsBefore.WorkingSet / 1048576.0, "Mio", $"privé {wsBefore.Private / 1048576.0:0.0} Mio");
            Add("Import", "Mémoire de travail après", wsAfter.WorkingSet / 1048576.0, "Mio", $"privé {wsAfter.Private / 1048576.0:0.0} Mio");
            Add("Import", "Pic de mémoire (processus)", wsAfter.PeakWorkingSet / 1048576.0, "Mio", "PeakWorkingSet64 depuis le démarrage");
            Add("Import", "Mémoire échantillonnée (pic)", sampler.MaxWorkingSet / 1048576.0, "Mio", $"{sampler.Callbacks} échantillons de progression");
            Add("Import", "Allocations managées", (allocAfter - allocBefore) / 1048576.0, "Mio", "GC.GetTotalAllocatedBytes");
            Add("Import", "WAL maximal observé", sampler.MaxWalBytes / 1048576.0, "Mio", "taille de catalog.db-wal pendant l'import");
            Add("Import", "Fichiers ouverts avant/après", wsAfter.Handles, "handles", $"avant {wsBefore.Handles}");
            foreach (var phase in sampler.PhaseSpans.OrderBy(pair => pair.Value.First))
            {
                Add("Import", $"Phase {phase.Key}", (phase.Value.Last - phase.Value.First).TotalMilliseconds, "ms",
                    $"{phase.Value.Count} notifications");
            }

            if (counters.Errors > 0)
            {
                Notes.Add($"L'import a produit {counters.Errors} erreur(s) : {string.Join(" | ", importResult.Diagnostics.Where(d => d.Severity == "error").Select(d => d.Code).Distinct())}.");
            }

            // État du stockage.
            var storage = await library.Catalog.GetStorageStatsAsync().ConfigureAwait(false);
            Add("Stockage", "Souvenirs", storage.Memories, "", $"{storage.Assets} assets, {storage.Sources} sources");
            Add("Stockage", "Médias sources", storage.SourceBytes / 1048576.0, "Mio", $"{storage.SourceBytes} octets");
            Add("Stockage", "Dérivés", storage.DerivedBytes / 1048576.0, "Mio", "miniatures incluses");
            Add("Stockage", "catalog.db", FileSize(Path.Combine(libraryRoot, "catalog.db")) / 1048576.0, "Mio", "hors WAL/SHM");
            Add("Stockage", "catalog.db-wal", FileSize(Path.Combine(libraryRoot, "catalog.db-wal")) / 1048576.0, "Mio", "après import, connexions fermées");
            Add("Stockage", "Dossier media/", DirSize(Path.Combine(libraryRoot, "media")) / 1048576.0, "Mio", $"{CountFiles(Path.Combine(libraryRoot, "media"))} fichiers");
            Add("Stockage", "Dossier imports/", DirSize(Path.Combine(libraryRoot, "imports")) / 1048576.0, "Mio", $"{CountFiles(Path.Combine(libraryRoot, "imports"))} fichiers temporaires restants");
            Add("Stockage", "Disque libre", FreeSpace(libraryRoot) / 1048576.0, "Mio", Path.GetPathRoot(Path.GetFullPath(libraryRoot))!);
            AddDatabaseInfo(libraryRoot);

            // Réouverture.
            await library.DisposeAsync().ConfigureAwait(false);
            library = null;
            ForceGc();
            var openTimes = new List<double>();
            for (var index = 0; index < 3; index++)
            {
                var watch = Stopwatch.StartNew();
                library = await MemoLibrary.OpenAsync(libraryRoot).ConfigureAwait(false);
                watch.Stop();
                openTimes.Add(watch.Elapsed.TotalMilliseconds);
                if (index < 2)
                {
                    await library.DisposeAsync().ConfigureAwait(false);
                    library = null;
                    ForceGc();
                }
            }

            Add("Bibliothèque", "Ouverture à froid", openTimes[0], "ms", "MemoLibrary.OpenAsync, 1re ouverture");
            Add("Bibliothèque", "Ouverture à chaud (médiane)", Median(openTimes.Skip(1).ToList()), "ms",
                $"min {openTimes.Skip(1).Min():0.0} ; max {openTimes.Skip(1).Max():0.0}");

            // Requêtes.
            var firstPage = await library.Catalog.QueryAsync(new MemoryQuery { Limit = 60 }).ConfigureAwait(false);
            await TimeQueryAsync("Requêtes", "Première page (60)", async () =>
                (await library.Catalog.QueryAsync(new MemoryQuery { Limit = 60 })).Count, iterations);
            await TimeQueryAsync("Requêtes", "Page 500 (toutes dates)", async () =>
                (await library.Catalog.QueryAsync(new MemoryQuery { Limit = 500 })).Count, iterations);
            await TimeQueryAsync("Requêtes", "Page 500, offset 500", async () =>
                (await library.Catalog.QueryAsync(new MemoryQuery { Limit = 500, Offset = 500 })).Count, iterations);
            await TimeQueryAsync("Requêtes", "Page 500, offset 1000", async () =>
                (await library.Catalog.QueryAsync(new MemoryQuery { Limit = 500, Offset = 1000 })).Count, iterations);
            await TimeQueryAsync("Requêtes", "Page 500, offset 5000", async () =>
                (await library.Catalog.QueryAsync(new MemoryQuery { Limit = 500, Offset = 5000 })).Count, iterations);
            await TimeQueryAsync("Requêtes", "Recherche large « a »", async () =>
                (await library.Catalog.QueryAsync(new MemoryQuery { Limit = 500, SearchText = "a" })).Count, iterations);
            var rareTerm = firstPage.FirstOrDefault()?.ExternalKey[..8] ?? "zzzz";
            await TimeQueryAsync("Requêtes", "Recherche précise (UUID)", async () =>
                (await library.Catalog.QueryAsync(new MemoryQuery { Limit = 500, SearchText = rareTerm })).Count, iterations, $"terme « {rareTerm} »");
            await TimeQueryAsync("Requêtes", "Recherche sans résultat", async () =>
                (await library.Catalog.QueryAsync(new MemoryQuery { Limit = 500, SearchText = "zzzz-introuvable" })).Count, iterations);
            await TimeQueryAsync("Requêtes", "CountAsync (tout)", async () =>
                await library.Catalog.CountAsync(new MemoryQuery()), iterations);
            await TimeQueryAsync("Requêtes", "CountAsync (recherche « a »)", async () =>
                await library.Catalog.CountAsync(new MemoryQuery { SearchText = "a" }), iterations);
            await TimeQueryAsync("Requêtes", "GetYearSummariesAsync", async () =>
                (await library.Catalog.GetYearSummariesAsync()).Count, iterations);
            await TimeQueryAsync("Requêtes", "GetMonthSummariesAsync", async () =>
                (await library.Catalog.GetMonthSummariesAsync()).Count, iterations);
            await TimeQueryAsync("Requêtes", "GetCardAsync (détail)", async () =>
                (await library.Catalog.GetCardAsync(firstPage[0].Id)) is null ? 0 : 1, iterations);
            await TimeQueryAsync("Requêtes", "GetDetailsAsync (détail)", async () =>
                (await library.Catalog.GetDetailsAsync(firstPage[0].Id)) is null ? 0 : 1, iterations);
            await TimeQueryAsync("Requêtes", "GetEarliestDateAsync", async () =>
                (await library.Catalog.GetEarliestDateAsync()) is null ? 0 : 1, iterations);
            await TimeQueryAsync("Requêtes", "GetLatestDateAsync", async () =>
                (await library.Catalog.GetLatestDateAsync()) is null ? 0 : 1, iterations);

            var thisDayAnchor = firstPage.FirstOrDefault(card => card.LocalDate is not null)?.LocalDate;
            var thisDay = thisDayAnchor is null
                ? new DateOnly(2024, 7, 14)
                : new DateOnly(2024, thisDayAnchor.Value.Month, thisDayAnchor.Value.Day);
            await TimeQueryAsync("Requêtes", $"GetThisDayAsync ({thisDay:MM-dd})", async () =>
                (await library.Discovery.GetThisDayAsync(thisDay, new ThisDayOptions())).Items.Count, iterations);
            await TimeQueryAsync("Requêtes", "GetThisDayAsync (02-29)", async () =>
                (await library.Discovery.GetThisDayAsync(new DateOnly(2024, 2, 29), new ThisDayOptions())).Items.Count, iterations);

            // Pagination profonde et mémoire d'une requête non bornée.
            ForceGc();
            var memBefore = Snapshot();
            var allocCards = GC.GetTotalAllocatedBytes(precise: true);
            var allWatch = Stopwatch.StartNew();
            var allCards = await library.Catalog.QueryAsync(new MemoryQuery { Limit = 100_000 }).ConfigureAwait(false);
            allWatch.Stop();
            var allocCardsAfter = GC.GetTotalAllocatedBytes(precise: true);
            var memAfterCards = Snapshot();
            Add("Mémoire", "QueryAsync Limit=100000", allWatch.Elapsed.TotalMilliseconds, "ms",
                $"{allCards.Count} cartes, {allocCardsAfter - allocCards:0} octets alloués");
            Add("Mémoire", "WS delta après 100000 cartes", (memAfterCards.WorkingSet - memBefore.WorkingSet) / 1048576.0, "Mio",
                $"avant {memBefore.WorkingSet / 1048576.0:0.0} ; après {memAfterCards.WorkingSet / 1048576.0:0.0} Mio");

            ForceGc();
            var handleBefore = Snapshot().Handles;
            for (var index = 0; index < 30; index++)
            {
                await library.Catalog.QueryAsync(new MemoryQuery { Limit = 60 }).ConfigureAwait(false);
            }

            ForceGc();
            var handleAfter = Snapshot().Handles;
            Add("Mémoire", "Handles après 30 requêtes", handleAfter, "handles", $"avant {handleBefore}, delta {handleAfter - handleBefore}");

            // Lieu : marque 10 % des souvenirs, puis requête filtrée.
            var located = allCards.Where((_, index) => index % 10 == 0).ToList();
            var locationTotal = await TimeOnceAsync(
                "Écritures",
                $"SetLocationAsync ×{located.Count}",
                async () =>
                {
                    for (var index = 0; index < located.Count; index++)
                    {
                        await library.Catalog.SetLocationAsync(
                            located[index].Id,
                            45.0 + (index % 100) * 0.01,
                            4.0 + (index % 100) * 0.01,
                            $"Lieu {index % 20}").ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            Add("Écritures", "SetLocationAsync par opération", locationTotal / Math.Max(1, located.Count), "ms", $"{located.Count} écritures");
            await TimeQueryAsync("Requêtes", "Avec lieu (page 500)", async () =>
                (await library.Catalog.QueryAsync(new MemoryQuery { Limit = 500, WithLocationOnly = true })).Count, iterations);

            // Miniatures photo.
            var photos = allCards
                .Where(card => card.Kind == MediaKind.Photo && card.MainAssetId is not null)
                .Take(photoTarget)
                .ToList();
            var photoPaths = new List<(MemoryCard Card, string Path)>(photos.Count);
            var resolveWatch = Stopwatch.StartNew();
            foreach (var card in photos)
            {
                var path = library.Catalog.GetAssetPath(card.MainAssetId!.Value);
                photoPaths.Add((card, path));
            }

            resolveWatch.Stop();
            Add("Miniatures", "Résolution des chemins", resolveWatch.Elapsed.TotalMilliseconds, "ms",
                $"{photoPaths.Count} GetAssetPath (connexion SQLite par appel)");

            DeleteDirectory(thumbsRoot);
            Directory.CreateDirectory(thumbsRoot);
            var photoTimes = new List<double>();
            var failures = new List<string>();
            long thumbnailBytes = 0;
            var thumbWsBefore = Snapshot();
            var thumbAllocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var thumbWatch = Stopwatch.StartNew();
            foreach (var (card, path) in photoPaths)
            {
                var destination = Path.Combine(thumbsRoot, $"{card.Id}.jpg");
                var watch = Stopwatch.StartNew();
                var ok = ThumbnailService.CreateImageThumbnail(path, destination, 512, out var diagnostic);
                watch.Stop();
                photoTimes.Add(watch.Elapsed.TotalMilliseconds);
                if (ok)
                {
                    thumbnailBytes += FileSize(destination);
                }
                else if (failures.Count < 5)
                {
                    failures.Add($"{card.Id}: {diagnostic}");
                }
            }

            thumbWatch.Stop();
            var thumbWsAfter = Snapshot();
            var thumbAllocAfter = GC.GetTotalAllocatedBytes(precise: true);
            Add("Miniatures", $"Photos ({photoPaths.Count}) — total", thumbWatch.Elapsed.TotalMilliseconds, "ms",
                $"{photoPaths.Count / Math.Max(thumbWatch.Elapsed.TotalSeconds, 0.001):0.0} images/s");
            Add("Miniatures", "Photo — médiane", photoTimes.Count == 0 ? 0 : Median(photoTimes), "ms",
                $"p95 {Percentile(photoTimes, 0.95):0.0} ; max {(photoTimes.Count == 0 ? 0 : photoTimes.Max()):0.0}");
            Add("Miniatures", "Photo — octets produits", thumbnailBytes / 1048576.0, "Mio", $"{thumbnailBytes} octets");
            Add("Miniatures", "Photo — WS delta", (thumbWsAfter.WorkingSet - thumbWsBefore.WorkingSet) / 1048576.0, "Mio",
                $"pic processus {thumbWsAfter.PeakWorkingSet / 1048576.0:0.0} Mio");
            Add("Miniatures", "Photo — allocations", (thumbAllocAfter - thumbAllocBefore) / 1048576.0, "Mio",
                $"{failures.Count} échec(s)");
            if (failures.Count > 0)
            {
                Notes.Add("Échecs de miniature photo : " + string.Join(" | ", failures));
            }

            // Miniatures vidéo : seulement si un MP4 décodable existe.
            var videos = allCards
                .Where(card => card.Kind == MediaKind.Video && card.MainAssetId is not null)
                .Take(20)
                .ToList();
            var videoAttempts = 0;
            var videoSuccesses = 0;
            var videoDiagnostics = new List<string>();
            if (videos.Count > 0)
            {
                try
                {
                    using var grabber = new VideoFrameGrabber();
                    foreach (var card in videos.Take(20))
                    {
                        videoAttempts++;
                        var source = library.Catalog.GetAssetPath(card.MainAssetId!.Value);
                        var destination = Path.Combine(thumbsRoot, $"video-{card.Id}.jpg");
                        var watch = Stopwatch.StartNew();
                        var ok = ThumbnailService.CreateVideoThumbnail(
                            grabber,
                            source,
                            destination,
                            512,
                            TimeSpan.FromSeconds(0.4),
                            out var diagnostic);
                        watch.Stop();
                        if (ok)
                        {
                            videoSuccesses++;
                            photoTimes.Add(watch.Elapsed.TotalMilliseconds);
                        }
                        else
                        {
                            videoDiagnostics.Add(diagnostic ?? "échec sans diagnostic");
                            if (videoAttempts >= 2)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    videoDiagnostics.Add(ex.Message);
                }
            }

            Add("Miniatures", $"Vidéos ({videoAttempts} tentatives)", videoSuccesses, "réussies",
                videoDiagnostics.Count == 0 ? "MP4 synthétiques décodables" : string.Join(" | ", videoDiagnostics.Take(2)));
            if (videoSuccesses == 0)
            {
                Notes.Add("Miniatures vidéo non mesurées : aucun MP4 réel/décodable dans le corpus synthétique (attendu).");
            }

            // Sauvegarde / restauration.
            DeleteFile(backupPath);
            var backupResult = await TimeOnceAsync(
                "Sauvegarde",
                "Création de la sauvegarde",
                async () =>
                {
                    await library.Backup.CreateBackupAsync(backupPath).ConfigureAwait(false);
                }).ConfigureAwait(false);
            var backupBytes = FileSize(backupPath);
            Add("Sauvegarde", "Taille de la sauvegarde", backupBytes / 1048576.0, "Mio", backupPath);
            Add("Sauvegarde", "Débit de sauvegarde", backupBytes / 1048576.0 / Math.Max(backupResult / 1000.0, 0.001), "Mio/s", $"{backupResult:0} ms");
            Add("Sauvegarde", "Disque libre après sauvegarde", FreeSpace(libraryRoot) / 1048576.0, "Mio", "");

            DeleteDirectory(restoreRoot);
            var restoreMs = await TimeOnceAsync(
                "Restauration",
                "Restauration complète",
                async () =>
                {
                    await library.Backup.RestoreAsync(backupPath, restoreRoot).ConfigureAwait(false);
                }).ConfigureAwait(false);
            var restoredSize = DirSize(restoreRoot);
            Add("Restauration", "Taille restaurée", restoredSize / 1048576.0, "Mio", $"{CountFiles(restoreRoot)} fichiers");
            Add("Restauration", "Débit de restauration", restoredSize / 1048576.0 / Math.Max(restoreMs / 1000.0, 0.001), "Mio/s", $"{restoreMs:0} ms");
            await using (var restored = await MemoLibrary.OpenAsync(restoreRoot).ConfigureAwait(false))
            {
                var restoredCount = await restored.Catalog.CountAsync(new MemoryQuery()).ConfigureAwait(false);
                Add("Restauration", "Souvenirs restaurés", restoredCount, "", $"attendu {storage.Memories}");
            }

            Add("Restauration", "Disque libre après restauration", FreeSpace(libraryRoot) / 1048576.0, "Mio", "");

            // Montée en charge du catalogue : duplique les lignes (médias partagés) pour
            // mesurer les requêtes au-delà de la taille du corpus, sans réimporter d'octets.
            if (catalogScale > 1)
            {
                await RunCatalogScaleAsync(libraryRoot, catalogScale, iterations).ConfigureAwait(false);
            }

            // Verrou SQLite : après fermeture, le fichier doit être ouvrable en exclusif.
            await library.DisposeAsync().ConfigureAwait(false);
            library = null;
            ForceGc();
            var lockDetail = "échec";
            try
            {
                using var handle = new FileStream(
                    Path.Combine(libraryRoot, "catalog.db"),
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                lockDetail = "catalog.db ouvrable en exclusif après fermeture";
            }
            catch (IOException ex)
            {
                lockDetail = "catalog.db encore verrouillé : " + ex.Message;
            }

            Add("Verrous", "Verrou SQLite après fermeture", lockDetail == "échec" ? 0 : 1, "ok",
                lockDetail);
            Add("Verrous", "WAL après fermeture", FileSize(Path.Combine(libraryRoot, "catalog.db-wal")), "octets", "SQLite supprime le WAL à la dernière fermeture propre");

            Add("Durée", "Harnais complet", started.Elapsed.TotalMilliseconds, "ms", "");
            Add("Durée", "Disque libre final", FreeSpace(libraryRoot) / 1048576.0, "Mio", "");

            WriteReport(outputPath, libraryRoot, corpusZips.Count, corpusBytes);
            PrintTable();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Échec du harnais : " + ex);
            return 1;
        }
        finally
        {
            if (library is not null)
            {
                await library.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task RunCatalogScaleAsync(string libraryRoot, int factor, int iterations)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(libraryRoot))!;
        var scaleRoot = Path.Combine(parent, "scale-library");
        DeleteDirectory(scaleRoot);
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(libraryRoot, "catalog.db"),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // La copie reste possible : le catalogue est fermé entre deux opérations.
        }

        var copyWatch = Stopwatch.StartNew();
        CopyDirectory(libraryRoot, scaleRoot);
        copyWatch.Stop();
        Add("Montée en charge", "Copie de la bibliothèque", copyWatch.Elapsed.TotalMilliseconds, "ms",
            $"{DirSize(scaleRoot) / 1048576.0:0.0} Mio copiés");

        var databasePath = Path.Combine(scaleRoot, "catalog.db");
        var duplicateWatch = Stopwatch.StartNew();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = databasePath,
                   Mode = SqliteOpenMode.ReadWrite,
                   Pooling = false,
                   DefaultTimeout = 30,
               }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            var ids = new List<long>();
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT id FROM memories ORDER BY id;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    ids.Add(reader.GetInt64(0));
                }
            }

            foreach (var id in ids)
            {
                for (var copy = 1; copy < factor; copy++)
                {
                    Execute(connection, transaction,
                        """
                        INSERT INTO memories (space_id, external_key, status, created_at, updated_at)
                        SELECT space_id, external_key || '-s' || @copy, status, created_at, updated_at
                          FROM memories WHERE id=@id;
                        """,
                        ("@copy", copy), ("@id", id));
                    var newId = Convert.ToInt64(Scalar(connection, transaction, "SELECT last_insert_rowid();"), Inv);

                    Execute(connection, transaction,
                        """
                        INSERT INTO date_candidates
                            (memory_id, raw, source, meaning, precision, timezone, instant_utc,
                             local_date, local_time, confidence)
                        SELECT @new, raw, source, meaning, precision, timezone, instant_utc,
                               local_date, local_time, confidence
                          FROM date_candidates WHERE memory_id=@id;
                        """,
                        ("@new", newId), ("@id", id));
                    Execute(connection, transaction,
                        "INSERT INTO memory_files (memory_id, source_file_id, role) SELECT @new, source_file_id, role FROM memory_files WHERE memory_id=@id;",
                        ("@new", newId), ("@id", id));
                    Execute(connection, transaction,
                        "INSERT OR IGNORE INTO memory_assets (memory_id, asset_id, role) SELECT @new, asset_id, role FROM memory_assets WHERE memory_id=@id;",
                        ("@new", newId), ("@id", id));
                    Execute(connection, transaction,
                        "INSERT OR IGNORE INTO thumbnails (memory_id, relative_path, updated_at) SELECT @new, relative_path, updated_at FROM thumbnails WHERE memory_id=@id;",
                        ("@new", newId), ("@id", id));
                    Execute(connection, transaction,
                        "INSERT OR IGNORE INTO locations (memory_id, lat, lon, label, source, updated_at) SELECT @new, lat, lon, label, source, updated_at FROM locations WHERE memory_id=@id;",
                        ("@new", newId), ("@id", id));
                    Execute(connection, transaction,
                        "INSERT OR IGNORE INTO date_overrides (memory_id, local_date, precision, note, updated_at) SELECT @new, local_date, precision, note, updated_at FROM date_overrides WHERE memory_id=@id;",
                        ("@new", newId), ("@id", id));
                    Execute(connection, transaction,
                        "INSERT OR IGNORE INTO memory_notes (memory_id, text, updated_at) SELECT @new, text, updated_at FROM memory_notes WHERE memory_id=@id;",
                        ("@new", newId), ("@id", id));
                    Execute(connection, transaction,
                        "INSERT OR IGNORE INTO analysis_ocr (memory_id, text, engine, updated_at) SELECT @new, text, engine, updated_at FROM analysis_ocr WHERE memory_id=@id;",
                        ("@new", newId), ("@id", id));
                    Execute(connection, transaction,
                        "INSERT OR IGNORE INTO favorites (memory_id, created_at) SELECT @new, created_at FROM favorites WHERE memory_id=@id;",
                        ("@new", newId), ("@id", id));
                    Execute(connection, transaction,
                        "INSERT OR IGNORE INTO exclusions (memory_id, scope, created_at) SELECT @new, scope, created_at FROM exclusions WHERE memory_id=@id;",
                        ("@new", newId), ("@id", id));
                }
            }

            transaction.Commit();
        }

        duplicateWatch.Stop();
        Add("Montée en charge", "Duplication des lignes", duplicateWatch.Elapsed.TotalMilliseconds, "ms",
            $"facteur ×{factor}");

        var openWatch = Stopwatch.StartNew();
        var scaled = await MemoLibrary.OpenAsync(scaleRoot).ConfigureAwait(false);
        openWatch.Stop();
        Add("Montée en charge", "Ouverture du catalogue agrandi", openWatch.Elapsed.TotalMilliseconds, "ms", $"×{factor}");

        try
        {
            var total = await scaled.Catalog.CountAsync(new MemoryQuery()).ConfigureAwait(false);
            Add("Montée en charge", "Souvenirs dans le catalogue agrandi", total, "", $"facteur ×{factor}");
            await TimeQueryAsync("Montée en charge", "Première page (60)", async () =>
                (await scaled.Catalog.QueryAsync(new MemoryQuery { Limit = 60 })).Count, iterations);
            await TimeQueryAsync("Montée en charge", "Page 500 (toutes dates)", async () =>
                (await scaled.Catalog.QueryAsync(new MemoryQuery { Limit = 500 })).Count, iterations);
            await TimeQueryAsync("Montée en charge", "Page 500, offset 5000", async () =>
                (await scaled.Catalog.QueryAsync(new MemoryQuery { Limit = 500, Offset = 5000 })).Count, iterations);
            await TimeQueryAsync("Montée en charge", "Page 500, offset 10000", async () =>
                (await scaled.Catalog.QueryAsync(new MemoryQuery { Limit = 500, Offset = 10000 })).Count, iterations);
            await TimeQueryAsync("Montée en charge", "Recherche large « a »", async () =>
                (await scaled.Catalog.QueryAsync(new MemoryQuery { Limit = 500, SearchText = "a" })).Count, iterations);
            await TimeQueryAsync("Montée en charge", "CountAsync (tout)", async () =>
                await scaled.Catalog.CountAsync(new MemoryQuery()), iterations);
            await TimeQueryAsync("Montée en charge", "GetYearSummariesAsync", async () =>
                (await scaled.Catalog.GetYearSummariesAsync()).Count, iterations);
            await TimeQueryAsync("Montée en charge", "GetThisDayAsync (12-31)", async () =>
                (await scaled.Discovery.GetThisDayAsync(new DateOnly(2024, 12, 31), new ThisDayOptions())).Items.Count, iterations);

            ForceGc();
            var before = Snapshot();
            var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            var all = await scaled.Catalog.QueryAsync(new MemoryQuery { Limit = 100_000 }).ConfigureAwait(false);
            watch.Stop();
            var allocAfter = GC.GetTotalAllocatedBytes(precise: true);
            var after = Snapshot();
            Add("Montée en charge", "QueryAsync Limit=100000", watch.Elapsed.TotalMilliseconds, "ms",
                $"{all.Count} cartes, {(allocAfter - allocBefore) / 1048576.0:0.0} Mio alloués, WS delta {(after.WorkingSet - before.WorkingSet) / 1048576.0:0.0} Mio");
        }
        finally
        {
            await scaled.DisposeAsync().ConfigureAwait(false);
            DeleteDirectory(scaleRoot);
        }
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private static async Task<double> TimeOnceAsync(string section, string name, Func<Task> action)
    {
        var watch = Stopwatch.StartNew();
        await action().ConfigureAwait(false);
        watch.Stop();
        Add(section, name, watch.Elapsed.TotalMilliseconds, "ms", "");
        return watch.Elapsed.TotalMilliseconds;
    }

    private static async Task TimeQueryAsync(
        string section,
        string name,
        Func<Task<int>> action,
        int iterations,
        string? detail = null)
    {
        // Une passe à blanc, puis les mesures.
        await action().ConfigureAwait(false);
        var times = new List<double>(iterations);
        var counts = new List<int>();
        for (var index = 0; index < iterations; index++)
        {
            var watch = Stopwatch.StartNew();
            var result = await action().ConfigureAwait(false);
            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
            counts.Add(result);
        }

        var suffix = detail is null ? "" : " ; " + detail;
        Add(
            section,
            name,
            Median(times),
            "ms",
            $"n={iterations} ; min {times.Min():0.0} ; max {times.Max():0.0} ; résultats {string.Join("/", counts.Distinct().OrderBy(value => value))}{suffix}");
    }

    private static void CaptureEnvironment(string libraryRoot, IReadOnlyList<string> corpusZips)
    {
        EnvironmentInfo["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        EnvironmentInfo["osVersion"] = Environment.OSVersion.VersionString;
        EnvironmentInfo["arch"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        EnvironmentInfo["dotnet"] = Environment.Version.ToString();
        EnvironmentInfo["processors"] = Environment.ProcessorCount;
        EnvironmentInfo["serverGc"] = System.Runtime.GCSettings.IsServerGC;
        EnvironmentInfo["timeZone"] = TimeZoneInfo.Local.Id;
        EnvironmentInfo["libraryRoot"] = Path.GetFullPath(libraryRoot);
        EnvironmentInfo["corpusArchives"] = corpusZips.Count;
        EnvironmentInfo["corpusBytes"] = corpusZips.Sum(path => new FileInfo(path).Length);
        EnvironmentInfo["startedAt"] = DateTimeOffset.Now.ToString("O", Inv);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(libraryRoot))!);
        EnvironmentInfo["diskTotalBytes"] = drive.TotalSize;
        EnvironmentInfo["diskFreeBytes"] = drive.AvailableFreeSpace;
        EnvironmentInfo["gcTotalMemory"] = GC.GetTotalMemory(false);

        Add("Environnement", "Processeurs logiques", Environment.ProcessorCount, "", EnvironmentInfo["os"]!.ToString()!);
        Add("Environnement", ".NET", 0, "", Environment.Version.ToString());
        Add("Environnement", "Disque libre initial", drive.AvailableFreeSpace / 1048576.0, "Mio", drive.Name);
        Add("Environnement", "Corpus", corpusZips.Count, "archives",
            $"{corpusZips.Sum(path => new FileInfo(path).Length) / 1048576.0:0.0} Mio");
    }

    private static void AddDatabaseInfo(string libraryRoot)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(libraryRoot, "catalog.db"),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            string Scalar(string sql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToString(command.ExecuteScalar(), Inv) ?? "";
            }

            Add("Stockage", "SQLite journal_mode", 0, "", Scalar("PRAGMA journal_mode;"));
            var pageCount = long.Parse(Scalar("PRAGMA page_count;"), Inv);
            var pageSize = long.Parse(Scalar("PRAGMA page_size;"), Inv);
            var freelist = long.Parse(Scalar("PRAGMA freelist_count;"), Inv);
            Add("Stockage", "Pages SQLite", pageCount, "pages", $"{pageSize} octets/page ; {freelist} libres ; {pageCount * pageSize / 1048576.0:0.0} Mio");
            Add("Stockage", "Intégrité SQLite", Scalar("PRAGMA integrity_check;") == "ok" ? 1 : 0, "ok", Scalar("PRAGMA quick_check;"));
        }
        catch (Exception ex)
        {
            Notes.Add("Inspection SQLite impossible : " + ex.Message);
        }
    }

    private static ProcessSnapshot Snapshot()
    {
        Proc.Refresh();
        return new ProcessSnapshot(
            Proc.WorkingSet64,
            Proc.PrivateMemorySize64,
            Proc.PeakWorkingSet64,
            Proc.HandleCount,
            GC.GetTotalMemory(false));
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void Add(string section, string name, double value, string unit, string detail) =>
        Metrics.Add(new Metric(section, name, value, unit, detail));

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(value => value).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(value => value).ToList();
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static long FileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static long DirSize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                total += FileSize(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return total;
    }

    private static int CountFiles(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static long FreeSpace(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void DeleteDirectory(string path)
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFile(string path)
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteReport(string outputPath, string libraryRoot, int archives, long corpusBytes)
    {
        var report = new
        {
            environment = EnvironmentInfo,
            libraryRoot,
            archives,
            corpusBytes,
            metrics = Metrics.Select(metric => new
            {
                section = metric.Section,
                name = metric.Name,
                value = metric.Value,
                unit = metric.Unit,
                detail = metric.Detail,
            }),
            notes = Notes,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"Résultats JSON : {outputPath}");
    }

    private static void PrintTable()
    {
        Console.WriteLine();
        Console.WriteLine("Mesures");
        Console.WriteLine("=======");
        foreach (var group in Metrics.GroupBy(metric => metric.Section))
        {
            Console.WriteLine();
            Console.WriteLine($"[{group.Key}]");
            foreach (var metric in group)
            {
                var value = metric.Unit switch
                {
                    "" => metric.Value.ToString("0", Inv),
                    "Mio" => metric.Value.ToString("0.0", Inv) + " Mio",
                    "ms" => metric.Value.ToString("0.0", Inv) + " ms",
                    _ => metric.Value.ToString("0.0", Inv) + " " + metric.Unit,
                };
                Console.WriteLine($"  {metric.Name,-44} {value,14}   {metric.Detail}");
            }
        }

        if (Notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Notes");
            Console.WriteLine("-----");
            foreach (var note in Notes)
            {
                Console.WriteLine("  - " + note);
            }
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Argument inattendu : " + key);
            }

            if (key == "--help")
            {
                result[key] = "1";
                continue;
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException("Valeur manquante pour " + key);
            }

            result[key] = args[++index];
        }

        return result;
    }

    private static string Require(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Option obligatoire : " + key);

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Harnais de mesure Memo (hors dépôt).
            Usage :
              dotnet run -c Release -- --library <dir> --corpus <dir[;dir]> --out <json>
                                    [--thumbs <dir>] [--backup <zip>] [--restore <dir>]
                                    [--photos 200] [--iterations 5] [--catalog-scale 8]\n                                    [--scale-library <dir>]
            """);
    }

    private sealed record ProcessSnapshot(long WorkingSet, long Private, long PeakWorkingSet, int Handles, long Managed);
}

internal sealed class ImportSampler : IProgress<ImportProgress>
{
    private readonly string _walPath;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Dictionary<ImportPhase, (DateTime First, DateTime Last, int Count)> _phases = new();

    public ImportSampler(string databasePath)
    {
        _walPath = databasePath + "-wal";
    }

    public long MaxWalBytes { get; private set; }
    public long MaxWorkingSet { get; private set; }
    public int Callbacks { get; private set; }
    public IReadOnlyDictionary<ImportPhase, (DateTime First, DateTime Last, int Count)> PhaseSpans => _phases;

    public void Report(ImportProgress value)
    {
        Callbacks++;
        try
        {
            var wal = File.Exists(_walPath) ? new FileInfo(_walPath).Length : 0;
            MaxWalBytes = Math.Max(MaxWalBytes, wal);
        }
        catch (IOException)
        {
        }

        try
        {
            _process.Refresh();
            MaxWorkingSet = Math.Max(MaxWorkingSet, _process.WorkingSet64);
        }
        catch (InvalidOperationException)
        {
        }

        var now = DateTime.UtcNow;
        if (_phases.TryGetValue(value.Phase, out var existing))
        {
            _phases[value.Phase] = (existing.First, now, existing.Count + 1);
        }
        else
        {
            _phases[value.Phase] = (now, now, 1);
        }
    }
}
'@
    [System.IO.File]::WriteAllText(
        (Join-Path $harnessRoot 'Program.cs'),
        $program,
        [System.Text.UTF8Encoding]::new($false))

    & dotnet build (Join-Path $harnessRoot 'MemoPerf.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Échec de la compilation du harnais (code $LASTEXITCODE)."
    }

    Write-Host ''
    Write-Host '=== 3/3 Import et mesures ===' -ForegroundColor Cyan
    Write-Host ("Bibliothèque : {0}" -f $libraryRoot)
    Write-Host 'Durée estimée : 10 à 20 min pour 1 200 souvenirs (génération incluse).'
    Write-Host ''

    $harnessArguments = @(
        'run', '-c', 'Release', '--no-build', '--project', $harnessRoot, '--',
        '--library', $libraryRoot,
        '--corpus', $corpusRoot,
        '--out', $resultsPath,
        '--thumbs', $thumbsRoot,
        '--backup', $backupPath,
        '--restore', $restoreRoot,
        '--photos', $Photos,
        '--iterations', $Iterations
    )
    if ($CatalogScale -gt 1) {
        $harnessArguments += @('--catalog-scale', $CatalogScale)
    }

    & dotnet @harnessArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Le harnais a échoué (code $LASTEXITCODE)."
    }

    if (-not (Test-Path -LiteralPath $resultsPath)) {
        throw "Le harnais n'a pas produit $resultsPath."
    }

    $report = Get-Content -LiteralPath $resultsPath -Raw | ConvertFrom-Json

    function Get-MetricValue {
        param([string]$Section, [string]$Name)
        $metric = $report.metrics |
            Where-Object { $_.section -eq $Section -and $_.name -eq $Name } |
            Select-Object -First 1
        if ($null -eq $metric) {
            return 0.0
        }

        return [double]$metric.value
    }

    function Get-MetricValueByPrefix {
        param([string]$Section, [string]$Prefix)
        $metric = $report.metrics |
            Where-Object { $_.section -eq $Section -and $_.name.StartsWith($Prefix) } |
            Select-Object -First 1
        if ($null -eq $metric) {
            return 0.0
        }

        return [double]$metric.value
    }

    $elapsed = (Get-Date) - $startedAt
    Write-Host ''
    Write-Host '=== Synthèse ===' -ForegroundColor Cyan
    Write-Host ("Souvenirs importés     : {0:0}" -f (Get-MetricValue 'Stockage' 'Souvenirs'))
    Write-Host ("Durée de l'import      : {0:0.0} s" -f ((Get-MetricValue 'Import' 'Durée totale') / 1000))
    Write-Host ("Première page (60)     : {0:0.0} ms" -f (Get-MetricValue 'Requêtes' 'Première page (60)'))
    Write-Host ("Page 500               : {0:0.0} ms" -f (Get-MetricValue 'Requêtes' 'Page 500 (toutes dates)'))
    Write-Host ("Recherche large        : {0:0.0} ms" -f (Get-MetricValue 'Requêtes' 'Recherche large « a »'))
    Write-Host ("CountAsync             : {0:0.0} ms" -f (Get-MetricValue 'Requêtes' 'CountAsync (tout)'))
    Write-Host ("GetThisDayAsync        : {0:0.0} ms" -f (Get-MetricValue 'Requêtes' 'GetThisDayAsync (02-29)'))
    Write-Host ("Miniatures photo       : {0:0.0} s" -f ((Get-MetricValueByPrefix 'Miniatures' 'Photos (') / 1000))
    Write-Host ("Sauvegarde             : {0:0.0} s" -f ((Get-MetricValue 'Sauvegarde' 'Création de la sauvegarde') / 1000))
    Write-Host ("Restauration           : {0:0.0} s" -f ((Get-MetricValue 'Restauration' 'Restauration complète') / 1000))
    Write-Host ("Durée totale du script : {0:0.0} min" -f $elapsed.TotalMinutes)
    Write-Host ("Résultats JSON         : {0}" -f $resultsPath)

    $completed = $true
}
finally {
    if ($completed -and -not $Keep) {
        Write-Host ''
        Write-Host "Nettoyage de $rootFull (utiliser -Keep pour conserver les artefacts)."
        Remove-Item -LiteralPath $rootFull -Recurse -Force -ErrorAction SilentlyContinue
    }
    elseif (-not $completed) {
        Write-Host ''
        Write-Host "Échec : les artefacts sont conservés dans $rootFull pour diagnostic." -ForegroundColor Yellow
    }
    elseif ($Keep) {
        Write-Host ''
        Write-Host "Artefacts conservés dans $rootFull."
    }
}
