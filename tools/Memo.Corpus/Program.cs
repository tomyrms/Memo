using System.Globalization;
using Memo.Corpus;

internal static class Program
{
    private const int DefaultCount = 40;

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // Sortie redirigée ou console sans handle : l'encodage par défaut suffit.
        }

        if (!TryParse(args, out var options, out var error))
        {
            if (error is not null)
            {
                Console.Error.WriteLine(error);
            }

            PrintUsage();
            return error is null ? 0 : 2;
        }

        try
        {
            var summary = CorpusGenerator.Generate(options!);
            PrintSummary(summary);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Échec de génération : {exception.Message}");
            return 1;
        }
    }

    private static bool TryParse(string[] args, out CorpusOptions? options, out string? error)
    {
        options = null;
        error = null;

        string? output = null;
        var seed = 42;
        var count = DefaultCount;
        var format = CorpusFormat.Zip;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--help" or "-h":
                    return false;

                case "--out" or "-o":
                    if (!TryTakeValue(args, ref index, out output))
                    {
                        error = "Valeur manquante pour --out.";
                        return false;
                    }

                    break;

                case "--seed":
                    if (!TryTakeValue(args, ref index, out var seedText) ||
                        !int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                    {
                        error = "Valeur entière attendue pour --seed.";
                        return false;
                    }

                    break;

                case "--count" or "-n":
                    if (!TryTakeValue(args, ref index, out var countText) ||
                        !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                    {
                        error = "Valeur entière attendue pour --count.";
                        return false;
                    }

                    break;

                case "--format" or "-f":
                    if (!TryTakeValue(args, ref index, out var formatText) ||
                        !Enum.TryParse<CorpusFormat>(formatText, ignoreCase: true, out format))
                    {
                        error = "Format attendu : zip ou folder.";
                        return false;
                    }

                    break;

                default:
                    error = $"Argument inconnu : {argument}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            error = "--out <dir> est obligatoire.";
            return false;
        }

        if (count is < 4 or > 500)
        {
            error = "--count doit être compris entre 4 et 500.";
            return false;
        }

        options = new CorpusOptions(Path.GetFullPath(output), seed, count, format);
        return true;
    }

    private static bool TryTakeValue(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            return false;
        }

        value = args[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Génère un corpus de souvenirs Snapchat entièrement synthétique.

            Usage :
              dotnet run --project tools/Memo.Corpus -- --out <dir> [options]

            Options :
              --out, -o <dir>       dossier de sortie (obligatoire)
              --seed <n>            graine déterministe (défaut : 42)
              --count, -n <n>       nombre de souvenirs, 4 à 500 (défaut : 40)
              --format, -f <mode>   zip (défaut) ou folder
              --help, -h            affiche cette aide

            Le dossier produit ne contient aucune donnée réelle. Les .zip générés
            sont ignorés par git et doivent être régénérés à la demande.
            """);
    }

    private static void PrintSummary(CorpusSummary summary)
    {
        Console.WriteLine("Corpus synthétique Memo");
        Console.WriteLine("=======================");
        Console.WriteLine($"Sortie       : {summary.OutputDirectory}");
        Console.WriteLine($"Graine       : {summary.Seed}");
        Console.WriteLine($"Format       : {summary.Format}");
        Console.WriteLine($"Souvenirs    : {summary.Memories} ({summary.Photos} photos, {summary.Videos} vidéos)");
        Console.WriteLine($"Dates        : {summary.Memories - summary.Undated} datés, {summary.Undated} sans date");
        Console.WriteLine($"Calques      : {summary.Overlays} (WebP {summary.WebpOverlays}, PNG {summary.PngOverlays})");
        Console.WriteLine($"Orphelins    : {summary.OrphanOverlays} calques sans média");
        Console.WriteLine($"Doublons     : {summary.DuplicateByteMemories} souvenir(s) partageant des octets identiques");
        Console.WriteLine();
        Console.WriteLine("Contenu généré :");

        foreach (var archive in summary.Archives)
        {
            Console.WriteLine(
                $"  {archive.Name,-24} {archive.Entries,4} entrée(s)  {FormatSize(archive.Bytes),10}  {archive.Description}");
        }

        Console.WriteLine();
        Console.WriteLine($"Cas couverts : {string.Join(", ", summary.Cases)}.");
        Console.WriteLine();
        Console.WriteLine("Aucune donnée réelle : le corpus peut être supprimé et régénéré avec la même graine.");
    }

    private static string FormatSize(long bytes)
    {
        const double kilo = 1024;
        const double mega = kilo * 1024;
        return bytes >= mega
            ? $"{bytes / mega:0.0} Mio"
            : $"{bytes / kilo:0.0} Kio";
    }
}
