using System.Globalization;
using System.Text.Json;

namespace Memo.Core;

/// <summary>
/// Adaptateur de test du format synthetique « memo-index-v1 ».
///
/// Ce format a ete invente pour exercer le contrat d'adaptateur et le service de telechargement.
/// Il ne decrit pas un export Snapchat, ne doit jamais etre presente comme tel, et n'est
/// enregistre par aucun chemin de production : <see cref="LinkIndexAdapterRegistry.Empty"/> ne le
/// contient pas et <see cref="IsValidated"/> vaut <c>false</c>.
///
/// Forme attendue, strictement identifiee par le marqueur <c>format</c> :
/// <code>
/// {
///   "format": "memo-index-v1",
///   "schemaVersion": 1,
///   "entries": [
///     {
///       "id": "synthetic-001",
///       "date": "2021-07-14T18:30:00Z",
///       "type": "photo",
///       "url": "https://media.example.test/synthetic/001.jpg",
///       "size": 12345,
///       "sha256": "0000..."
///     }
///   ]
/// }
/// </code>
/// </summary>
internal sealed class MemoIndexV1Adapter : ILinkIndexAdapter
{
    public const string FormatName = "memo-index-v1";
    public const int SchemaVersion = 1;

    public string Id => FormatName;

    /// <summary>Format synthetique : jamais valide comme format d'export reel.</summary>
    public bool IsValidated => false;

    public LinkIndexDetection Detect(ReadOnlySpan<byte> content)
    {
        if (!TryReadHeader(content, out var document, out var reason))
        {
            return new LinkIndexDetection(false, Id, reason);
        }

        document!.Dispose();
        return new LinkIndexDetection(
            true,
            Id,
            $"Marqueur {FormatName} et schema {SchemaVersion} reconnus.");
    }

    public LinkIndexResult Read(ReadOnlyMemory<byte> content, LinkIndexReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TryReadHeader(content.Span, out var document, out var reason))
        {
            return LinkIndexResult.NotRecognized(
                Id,
                "index.unknown-shape",
                reason ?? "Marqueur absent.");
        }

        using (var parsed = document!)
        {
            var diagnostics = new List<LinkIndexDiagnostic>();
            var entries = new List<LinkIndexEntry>();
            var root = parsed.RootElement;
            if (!root.TryGetProperty("entries", out var entriesElement) ||
                entriesElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(new LinkIndexDiagnostic(
                    LinkIndexDiagnostic.Error,
                    "index.entries-missing",
                    "Le tableau « entries » est absent."));
                return new LinkIndexResult(Id, true, entries, diagnostics);
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var position = 0;
            foreach (var element in entriesElement.EnumerateArray())
            {
                position++;
                if (position > options.MaxEntries)
                {
                    diagnostics.Add(new LinkIndexDiagnostic(
                        LinkIndexDiagnostic.Error,
                        "index.entry-limit",
                        $"Index tronque : plus de {options.MaxEntries} entrees."));
                    break;
                }

                ReadEntry(element, position, options, seenIds, entries, diagnostics);
            }

            return new LinkIndexResult(Id, true, entries, diagnostics);
        }
    }

    private static bool TryReadHeader(
        ReadOnlySpan<byte> content,
        out JsonDocument? document,
        out string? reason)
    {
        document = null;
        reason = null;
        if (content.IsEmpty)
        {
            reason = "Contenu vide.";
            return false;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(content.ToArray());
        }
        catch (JsonException)
        {
            reason = "JSON invalide.";
            return false;
        }

        var root = parsed.RootElement;
        var valid =
            root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("format", out var format) &&
            format.ValueKind == JsonValueKind.String &&
            string.Equals(format.GetString(), FormatName, StringComparison.Ordinal) &&
            root.TryGetProperty("schemaVersion", out var schema) &&
            schema.ValueKind == JsonValueKind.Number &&
            schema.TryGetInt32(out var version) &&
            version == SchemaVersion;
        if (!valid)
        {
            parsed.Dispose();
            reason = $"Marqueur {FormatName} version {SchemaVersion} absent.";
            return false;
        }

        document = parsed;
        return true;
    }

    private static void ReadEntry(
        JsonElement element,
        int position,
        LinkIndexReadOptions options,
        HashSet<string> seenIds,
        List<LinkIndexEntry> entries,
        List<LinkIndexDiagnostic> diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new LinkIndexDiagnostic(
                LinkIndexDiagnostic.Error,
                "index.entry-shape",
                $"Entree #{position} : objet JSON attendu."));
            return;
        }

        string? id = null;
        if (element.TryGetProperty("id", out var idElement) &&
            idElement.ValueKind == JsonValueKind.String)
        {
            id = idElement.GetString()?.Trim();
        }

        if (string.IsNullOrEmpty(id))
        {
            diagnostics.Add(new LinkIndexDiagnostic(
                LinkIndexDiagnostic.Error,
                "index.entry-id",
                $"Entree #{position} : identifiant manquant."));
            return;
        }

        if (!seenIds.Add(id))
        {
            diagnostics.Add(new LinkIndexDiagnostic(
                LinkIndexDiagnostic.Error,
                "index.entry-duplicate",
                "Identifiant d'entree en double.",
                id));
            return;
        }

        DateTimeOffset? date = null;
        if (element.TryGetProperty("date", out var dateElement))
        {
            if (dateElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    dateElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedDate))
            {
                date = parsedDate;
            }
            else
            {
                diagnostics.Add(new LinkIndexDiagnostic(
                    LinkIndexDiagnostic.Warning,
                    "index.entry-date",
                    "Date d'index illisible ; elle est ignoree.",
                    id));
            }
        }

        var type = LinkMediaType.Unknown;
        if (element.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String)
        {
            type = typeElement.GetString()?.ToLowerInvariant() switch
            {
                "photo" => LinkMediaType.Photo,
                "video" => LinkMediaType.Video,
                _ => LinkMediaType.Unknown,
            };
        }

        if (type == LinkMediaType.Unknown)
        {
            diagnostics.Add(new LinkIndexDiagnostic(
                LinkIndexDiagnostic.Error,
                "index.entry-type",
                "Type attendu : « photo » ou « video ».",
                id));
            return;
        }

        if (!element.TryGetProperty("url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new LinkIndexDiagnostic(
                LinkIndexDiagnostic.Error,
                "index.entry-url",
                "URL absente.",
                id));
            return;
        }

        if (!Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url))
        {
            diagnostics.Add(new LinkIndexDiagnostic(
                LinkIndexDiagnostic.Error,
                "link.url-invalid",
                "URL absolue invalide.",
                id));
            return;
        }

        if (!LinkUrlValidator.TryValidate(url, options.UrlPolicy, out var code, out var message))
        {
            diagnostics.Add(new LinkIndexDiagnostic(
                LinkIndexDiagnostic.Error,
                code,
                message,
                id,
                LinkUrlValidator.SafeHost(url)));
            return;
        }

        long? expectedSize = null;
        if (element.TryGetProperty("size", out var sizeElement) &&
            sizeElement.ValueKind != JsonValueKind.Null)
        {
            if (sizeElement.ValueKind != JsonValueKind.Number ||
                !sizeElement.TryGetInt64(out var size) ||
                size <= 0)
            {
                diagnostics.Add(new LinkIndexDiagnostic(
                    LinkIndexDiagnostic.Error,
                    "index.entry-size",
                    "Taille attendue invalide.",
                    id,
                    url.Host));
                return;
            }

            if (size > options.MaxEntryBytes)
            {
                diagnostics.Add(new LinkIndexDiagnostic(
                    LinkIndexDiagnostic.Error,
                    "link.size-exceeded",
                    "Taille attendue superieure a la limite configuree.",
                    id,
                    url.Host));
                return;
            }

            expectedSize = size;
        }

        string? expectedSha256 = null;
        if (element.TryGetProperty("sha256", out var hashElement) &&
            hashElement.ValueKind != JsonValueKind.Null)
        {
            var hash = hashElement.ValueKind == JsonValueKind.String
                ? hashElement.GetString()?.Trim().ToLowerInvariant()
                : null;
            if (hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            {
                diagnostics.Add(new LinkIndexDiagnostic(
                    LinkIndexDiagnostic.Error,
                    "index.entry-hash",
                    "Empreinte SHA-256 attendue invalide.",
                    id,
                    url.Host));
                return;
            }

            expectedSha256 = hash;
        }

        entries.Add(new LinkIndexEntry(id, date, type, url, expectedSize, expectedSha256));
    }
}
