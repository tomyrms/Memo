using System.Globalization;
using System.Text.Json.Serialization;

namespace Memo.Core;

/// <summary>
/// Type de média déclaré par un index. Il ne remplace jamais la détection binaire du contenu.
/// </summary>
internal enum LinkMediaType
{
    Unknown = 0,
    Photo = 1,
    Video = 2,
}

/// <summary>
/// Entrée d'index de lien. La date et le type sont ceux déclarés par l'index : ils ne sont pas
/// promus en métadonnées certaines tant que le format n'a pas été validé sur un exemple réel.
/// </summary>
internal sealed record LinkIndexEntry(
    string Id,
    DateTimeOffset? Date,
    LinkMediaType Type,
    [property: JsonIgnore] Uri Url,
    long? ExpectedSize = null,
    string? ExpectedSha256 = null)
{
    /// <summary>URL expurgée, utilisable dans un journal ou un rapport.</summary>
    public string SafeUrl => LinkUrlSafety.ToSafeDisplay(Url);

    public override string ToString() =>
        "LinkIndexEntry { " +
        $"Id = {Id}, " +
        $"Date = {Date?.ToString("O", CultureInfo.InvariantCulture) ?? "null"}, " +
        $"Type = {Type}, " +
        $"SafeUrl = {SafeUrl}, " +
        $"ExpectedSize = {ExpectedSize?.ToString(CultureInfo.InvariantCulture) ?? "null"}, " +
        $"ExpectedSha256 = {ExpectedSha256 ?? "null"} " +
        "}";
}

/// <summary>
/// Diagnostic d'index ou de téléchargement. Il ne contient jamais l'URL complète : seulement
/// l'identifiant d'entrée et, au besoin, le domaine. Les jetons et query strings signées restent
/// hors des rapports et des journaux.
/// </summary>
internal sealed record LinkIndexDiagnostic(
    string Severity,
    string Code,
    string Message,
    string? EntryId = null,
    string? Host = null)
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";
}

/// <summary>Résultat d'une détection d'adaptateur.</summary>
internal sealed record LinkIndexDetection(
    bool Detected,
    string? AdapterId,
    string? Reason);

/// <summary>Résultat de lecture d'un index par un adaptateur.</summary>
internal sealed record LinkIndexResult(
    string AdapterId,
    bool Recognized,
    IReadOnlyList<LinkIndexEntry> Entries,
    IReadOnlyList<LinkIndexDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.Severity == LinkIndexDiagnostic.Error);

    public static LinkIndexResult NotRecognized(string adapterId, string code, string message) =>
        new(
            adapterId,
            false,
            Array.Empty<LinkIndexEntry>(),
            new[] { new LinkIndexDiagnostic(LinkIndexDiagnostic.Warning, code, message) });
}

/// <summary>Limites et politique d'URL appliquées pendant la lecture d'un index.</summary>
internal sealed record LinkIndexReadOptions(
    LinkUrlPolicy UrlPolicy,
    int MaxEntries = 10_000,
    long MaxEntryBytes = 4L * 1024 * 1024 * 1024,
    int MaxIndexBytes = 8 * 1024 * 1024);
