using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Memo.Core;

/// <summary>
/// Politique d'un téléchargement opt-in. Les domaines autorisés viennent d'un format validé, jamais
/// d'une URL trouvée dans une archive non reconnue.
/// </summary>
internal sealed record LinkDownloadPolicy(
    LinkUrlPolicy UrlPolicy,
    long MaxBytes = 2L * 1024 * 1024 * 1024,
    TimeSpan? RequestTimeout = null,
    int MaxRedirects = 3);

internal sealed record LinkDownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    double? Fraction)
{
    public static LinkDownloadProgress Create(long received, long? total) =>
        new(
            received,
            total,
            total is > 0
                ? Math.Clamp(received / (double)total.Value, 0d, 1d)
                : null);
}

internal enum LinkDownloadStatus
{
    Completed = 1,
    AlreadyPresent = 2,
    Failed = 3,
}

internal sealed record LinkDownloadResult(
    LinkDownloadStatus Status,
    string? Sha256,
    long Bytes,
    string? RelativePath,
    IReadOnlyList<LinkIndexDiagnostic> Diagnostics)
{
    public bool Success => Status is LinkDownloadStatus.Completed or LinkDownloadStatus.AlreadyPresent;

    public static LinkDownloadResult Failed(
        string code,
        string message,
        string? entryId = null,
        string? host = null,
        long bytes = 0) =>
        new(
            LinkDownloadStatus.Failed,
            null,
            bytes,
            null,
            new[] { new LinkIndexDiagnostic(LinkIndexDiagnostic.Error, code, message, entryId, host) });
}

/// <summary>
/// Téléchargement opt-in d'une entrée d'index vers le stockage content-addressed de la
/// bibliothèque (<c>media/&lt;préfixe&gt;/&lt;sha256&gt;.&lt;ext&gt;</c>).
///
/// Ce service n'est branché sur aucun import par défaut. Il n'est utilisable qu'après activation
/// explicite d'un adaptateur validé et d'une politique de domaines. Aucune URL complète, aucun
/// jeton et aucune query string signée n'apparaissent dans les diagnostics.
/// </summary>
internal sealed class LinkDownloadService : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly string _rootPath;
    private readonly LinkDownloadPolicy _policy;
    private readonly HttpClient _http;

    public LinkDownloadService(
        MemoCatalog catalog,
        LinkDownloadPolicy policy,
        HttpMessageHandler? handler = null)
        : this(
            catalog?.RootPath ?? throw new ArgumentNullException(nameof(catalog)),
            policy,
            handler)
    {
    }

    public LinkDownloadService(
        string rootPath,
        LinkDownloadPolicy policy,
        HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(policy);
        if (handler is SocketsHttpHandler sockets && sockets.AllowAutoRedirect)
        {
            throw new ArgumentException(
                "Le handler doit desactiver les redirections automatiques.",
                nameof(handler));
        }

        if (handler is HttpClientHandler client && client.AllowAutoRedirect)
        {
            throw new ArgumentException(
                "Le handler doit desactiver les redirections automatiques.",
                nameof(handler));
        }

        _rootPath = Path.GetFullPath(rootPath);
        _policy = policy;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = policy.RequestTimeout ?? DefaultTimeout;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Memo/0.1");
    }

    public void Dispose() => _http.Dispose();

    public async Task<LinkDownloadResult> DownloadAsync(
        LinkIndexEntry entry,
        IProgress<LinkDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            return LinkDownloadResult.Failed(
                "link.entry-id",
                "Identifiant d'entree manquant.");
        }

        if (!LinkUrlValidator.TryValidate(entry.Url, _policy.UrlPolicy, out var code, out var message))
        {
            return LinkDownloadResult.Failed(code, message, entry.Id, LinkUrlValidator.SafeHost(entry.Url));
        }

        if (entry.Type == LinkMediaType.Unknown)
        {
            return LinkDownloadResult.Failed(
                "link.type-unknown",
                "Type de media inconnu.",
                entry.Id,
                entry.Url.Host);
        }

        if (entry.ExpectedSize is { } declaredSize && declaredSize > _policy.MaxBytes)
        {
            return LinkDownloadResult.Failed(
                "link.size-exceeded",
                "Taille annoncee superieure a la limite configuree.",
                entry.Id,
                entry.Url.Host);
        }

        if (entry.ExpectedSha256 is { } expectedHash && !IsSha256(expectedHash))
        {
            return LinkDownloadResult.Failed(
                "link.hash-invalid",
                "Empreinte SHA-256 attendue invalide.",
                entry.Id,
                entry.Url.Host);
        }

        var partPath = GetPartPath(entry);
        long existing;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
            existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return LinkDownloadResult.Failed(
                "link.storage-error",
                "Acces au stockage local impossible.",
                entry.Id,
                entry.Url.Host);
        }

        if (existing > 0 && entry.ExpectedSize is { } expectedTotal && existing > expectedTotal)
        {
            TryDelete(partPath);
            existing = 0;
        }

        progress?.Report(LinkDownloadProgress.Create(existing, entry.ExpectedSize));

        if (existing > 0 && entry.ExpectedSize is { } completeSize && existing == completeSize)
        {
            var finalized = await TryFinalizeSafeAsync(entry, partPath, ct).ConfigureAwait(false);
            if (finalized is not null)
            {
                return finalized;
            }

            TryDelete(partPath);
            existing = 0;
        }

        var currentUri = entry.Url;
        var received = existing;
        HttpResponseMessage? response = null;
        try
        {
            for (var redirect = 0; ; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                if (existing > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existing, null);
                }

                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!IsRedirect(response.StatusCode))
                {
                    break;
                }

                if (redirect >= _policy.MaxRedirects)
                {
                    return LinkDownloadResult.Failed(
                        "link.redirect-limit",
                        "Trop de redirections.",
                        entry.Id,
                        LinkUrlValidator.SafeHost(response.Headers.Location));
                }

                var location = response.Headers.Location;
                if (location is null)
                {
                    return LinkDownloadResult.Failed(
                        "link.redirect-refused",
                        "Redirection sans destination.",
                        entry.Id);
                }

                Uri target;
                try
                {
                    target = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                }
                catch (UriFormatException)
                {
                    return LinkDownloadResult.Failed(
                        "link.redirect-refused",
                        "Redirection invalide.",
                        entry.Id);
                }

                if (!IsSameOrigin(currentUri, target) ||
                    !LinkUrlValidator.TryValidate(target, _policy.UrlPolicy, out _, out _))
                {
                    return LinkDownloadResult.Failed(
                        "link.redirect-refused",
                        "Redirection refusee : la destination sort de l'origine autorisee.",
                        entry.Id,
                        LinkUrlValidator.SafeHost(target));
                }

                response.Dispose();
                response = null;
                currentUri = target;
            }

            var http = response!;
            var host = LinkUrlValidator.SafeHost(currentUri);
            if (http.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (entry.ExpectedSize is { } expectedRangeSize && existing == expectedRangeSize)
                {
                    var finalized = await TryFinalizeSafeAsync(entry, partPath, ct).ConfigureAwait(false);
                    if (finalized is not null)
                    {
                        return finalized;
                    }
                }

                TryDelete(partPath);
                return LinkDownloadResult.Failed(
                    "link.range-invalid",
                    "Le serveur refuse la reprise demandee.",
                    entry.Id,
                    host);
            }

            if (!http.IsSuccessStatusCode)
            {
                return MapHttpFailure(http, entry, existing, host);
            }

            if (http.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            {
                return LinkDownloadResult.Failed(
                    "link.http-error",
                    $"Reponse HTTP inattendue ({(int)http.StatusCode}).",
                    entry.Id,
                    host);
            }

            var append = http.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (http.StatusCode == HttpStatusCode.PartialContent)
            {
                var from = http.Content.Headers.ContentRange?.From;
                if (from is { } start && start != existing)
                {
                    TryDelete(partPath);
                    return LinkDownloadResult.Failed(
                        "link.range-invalid",
                        "La plage renvoyee ne correspond pas a la reprise demandee.",
                        entry.Id,
                        host);
                }
            }
            else if (existing > 0)
            {
                // Le serveur a ignore Range : on repart de zero plutot que d'accoler deux contenus.
                existing = 0;
                append = false;
            }

            var contentLength = http.Content.Headers.ContentLength;
            var totalBytes = http.StatusCode == HttpStatusCode.PartialContent
                ? http.Content.Headers.ContentRange?.Length ??
                  (contentLength is null ? null : existing + contentLength)
                : contentLength;
            if (totalBytes is { } announcedTotal && announcedTotal > _policy.MaxBytes)
            {
                TryDelete(partPath);
                return LinkDownloadResult.Failed(
                    "link.size-exceeded",
                    "Contenu plus grand que la limite configuree.",
                    entry.Id,
                    host);
            }

            if (totalBytes is { } advertisedTotal &&
                entry.ExpectedSize is { } expectedAnnounced &&
                advertisedTotal != expectedAnnounced)
            {
                TryDelete(partPath);
                return LinkDownloadResult.Failed(
                    "link.size-mismatch",
                    "Taille annoncee par le serveur differente de celle de l'index.",
                    entry.Id,
                    host);
            }

            received = append ? existing : 0;
            var tooLarge = false;
            await using (var output = new FileStream(
                partPath,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var input = await http.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    while (true)
                    {
                        var read = await input
                            .ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                            .ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        received += read;
                        if (received > _policy.MaxBytes)
                        {
                            tooLarge = true;
                            break;
                        }

                        await output
                            .WriteAsync(buffer.AsMemory(0, read), ct)
                            .ConfigureAwait(false);
                        progress?.Report(LinkDownloadProgress.Create(received, totalBytes));
                    }

                    if (!tooLarge)
                    {
                        await output.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (tooLarge)
            {
                TryDelete(partPath);
                return LinkDownloadResult.Failed(
                    "link.size-exceeded",
                    "Telechargement interrompu : limite de taille depassee.",
                    entry.Id,
                    host,
                    received);
            }

            if (totalBytes is { } expectedTotalBytes && received != expectedTotalBytes)
            {
                return LinkDownloadResult.Failed(
                    "link.network-error",
                    "Transfert incomplet ; reprise possible.",
                    entry.Id,
                    host,
                    received);
            }

            if (entry.ExpectedSize is { } expectedFinalSize && received != expectedFinalSize)
            {
                TryDelete(partPath);
                return LinkDownloadResult.Failed(
                    "link.size-mismatch",
                    "Taille recue differente de la taille annoncee.",
                    entry.Id,
                    host,
                    received);
            }

            var result = await TryFinalizeSafeAsync(entry, partPath, ct).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }

            TryDelete(partPath);
            return LinkDownloadResult.Failed(
                "link.size-mismatch",
                "Taille finale incoherente.",
                entry.Id,
                host,
                received);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return LinkDownloadResult.Failed(
                "link.storage-error",
                "Acces au stockage local refuse.",
                entry.Id,
                LinkUrlValidator.SafeHost(currentUri),
                received);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return LinkDownloadResult.Failed(
                "link.network-error",
                "Erreur reseau pendant le telechargement ; reprise possible.",
                entry.Id,
                LinkUrlValidator.SafeHost(currentUri),
                received);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<LinkDownloadResult?> TryFinalizeSafeAsync(
        LinkIndexEntry entry,
        string partPath,
        CancellationToken ct)
    {
        try
        {
            return await TryFinalizeAsync(entry, partPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return LinkDownloadResult.Failed(
                "link.storage-error",
                "Acces au stockage local impossible.",
                entry.Id,
                LinkUrlValidator.SafeHost(entry.Url));
        }
    }

    private async Task<LinkDownloadResult?> TryFinalizeAsync(
        LinkIndexEntry entry,
        string partPath,
        CancellationToken ct)
    {
        if (!File.Exists(partPath))
        {
            return null;
        }

        var size = new FileInfo(partPath).Length;
        if (entry.ExpectedSize is { } expected && size != expected)
        {
            return null;
        }

        string sha256;
        await using (var stream = new FileStream(
            partPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        }

        if (entry.ExpectedSha256 is { } expectedSha &&
            !string.Equals(expectedSha, sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(partPath);
            return LinkDownloadResult.Failed(
                "link.hash-mismatch",
                "L'empreinte SHA-256 du contenu ne correspond pas a celle de l'index.",
                entry.Id,
                LinkUrlValidator.SafeHost(entry.Url),
                size);
        }

        MediaInfo info;
        try
        {
            using var stream = new FileStream(
                partPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            info = MediaInspector.Inspect(stream, out _);
        }
        catch (Exception)
        {
            TryDelete(partPath);
            return LinkDownloadResult.Failed(
                "link.format-unknown",
                "Contenu illisible comme media.",
                entry.Id,
                LinkUrlValidator.SafeHost(entry.Url),
                size);
        }

        if (string.Equals(info.Format, "unknown", StringComparison.Ordinal))
        {
            TryDelete(partPath);
            return LinkDownloadResult.Failed(
                "link.format-unknown",
                "Le contenu telecharge n'est pas un media reconnu.",
                entry.Id,
                LinkUrlValidator.SafeHost(entry.Url),
                size);
        }

        var extension = MemoCatalog.CanonicalExtension(info.Format);
        var relativePath = $"media/{sha256[..2]}/{sha256}.{extension}";
        var finalPath = ImportPaths.ToPlatformPath(_rootPath, relativePath);
        if (!ImportPaths.IsInsideRoot(_rootPath, finalPath))
        {
            TryDelete(partPath);
            return LinkDownloadResult.Failed(
                "link.storage-path",
                "Chemin de stockage refuse.",
                entry.Id,
                LinkUrlValidator.SafeHost(entry.Url),
                size);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        if (File.Exists(finalPath))
        {
            TryDelete(partPath);
            return new LinkDownloadResult(
                LinkDownloadStatus.AlreadyPresent,
                sha256,
                size,
                relativePath,
                Array.Empty<LinkIndexDiagnostic>());
        }

        try
        {
            File.Move(partPath, finalPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            TryDelete(partPath);
            return new LinkDownloadResult(
                LinkDownloadStatus.AlreadyPresent,
                sha256,
                size,
                relativePath,
                Array.Empty<LinkIndexDiagnostic>());
        }

        return new LinkDownloadResult(
            LinkDownloadStatus.Completed,
            sha256,
            size,
            relativePath,
            Array.Empty<LinkIndexDiagnostic>());
    }

    private static LinkDownloadResult MapHttpFailure(
        HttpResponseMessage response,
        LinkIndexEntry entry,
        long bytes,
        string? host)
    {
        var status = (int)response.StatusCode;
        var code = response.StatusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => "link.expired",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "link.forbidden",
            >= HttpStatusCode.InternalServerError => "link.server-error",
            _ => "link.http-error",
        };
        var message = code switch
        {
            "link.expired" => $"Lien expire ou introuvable (HTTP {status}).",
            "link.forbidden" => $"Acces refuse par le serveur (HTTP {status}).",
            "link.server-error" => $"Erreur du serveur distant (HTTP {status}).",
            _ => $"Reponse HTTP inattendue ({status}).",
        };
        return LinkDownloadResult.Failed(code, message, entry.Id, host, bytes);
    }

    private string GetPartPath(LinkIndexEntry entry)
    {
        // L'identite inclut l'URL complete mais seul son empreinte apparait dans le nom du fichier.
        var identity = entry.Id + "\n" + entry.Url.AbsoluteUri;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return Path.Combine(_rootPath, "imports", $"download-{key}.part");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);

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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
