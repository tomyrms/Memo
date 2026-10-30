using System.Globalization;

namespace Memo.Core;

/// <summary>
/// Domaines autorisés pour un index ou un téléchargement. La comparaison est exacte et insensible
/// à la casse : aucun sous-domaine ni caractère générique n'est accepté implicitement.
/// </summary>
internal sealed class LinkUrlPolicy
{
    private readonly HashSet<string> _allowedHosts;

    public LinkUrlPolicy(IEnumerable<string> allowedHosts, bool allowInsecureLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        _allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in allowedHosts)
        {
            var host = NormalizeHost(candidate);
            if (host.Length == 0)
            {
                throw new ArgumentException("Un domaine autorisé ne peut pas être vide.", nameof(allowedHosts));
            }

            _allowedHosts.Add(host);
        }

        if (_allowedHosts.Count == 0)
        {
            throw new ArgumentException("Au moins un domaine autorisé est requis.", nameof(allowedHosts));
        }

        AllowInsecureLoopback = allowInsecureLoopback;
    }

    /// <summary>
    /// Autorise HTTP uniquement sur une adresse de boucle locale. Cette option existe pour le
    /// serveur de test local ; elle reste désactivée par défaut.
    /// </summary>
    public bool AllowInsecureLoopback { get; }

    public IReadOnlyCollection<string> AllowedHosts => _allowedHosts;

    public static string NormalizeHost(string? host) =>
        (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    public bool IsHostAllowed(string? host) => _allowedHosts.Contains(NormalizeHost(host));
}

/// <summary>Validation commune aux index et aux téléchargements.</summary>
internal static class LinkUrlValidator
{
    public static bool TryValidate(
        Uri? uri,
        LinkUrlPolicy policy,
        out string code,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(policy);
        code = string.Empty;
        message = string.Empty;

        if (uri is null || !uri.IsAbsoluteUri)
        {
            code = "link.url-invalid";
            message = "URL absente ou non absolue.";
            return false;
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp =
            string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            policy.AllowInsecureLoopback &&
            uri.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
        {
            code = "link.url-scheme";
            message = "Seules les URL HTTPS sont acceptées.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            code = "link.url-userinfo";
            message = "Les URL contenant des identifiants sont refusées.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            code = "link.url-host";
            message = "URL sans domaine.";
            return false;
        }

        if (!policy.IsHostAllowed(uri.Host))
        {
            code = "link.url-host";
            message = "Domaine non autorisé.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            code = "link.url-fragment";
            message = "Les fragments d'URL sont refusés.";
            return false;
        }

        return true;
    }

    public static string? SafeHost(Uri? uri) =>
        uri is not null && uri.IsAbsoluteUri && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : null;
}

/// <summary>Réduction d'une URL à une forme sûre pour les journaux et les rapports.</summary>
internal static class LinkUrlSafety
{
    public static string ToSafeDisplay(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return "<url invalide>";
        }

        var port = uri.IsDefaultPort
            ? string.Empty
            : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return $"{uri.Scheme}://{uri.Host}{port}/<redacted>";
    }
}
