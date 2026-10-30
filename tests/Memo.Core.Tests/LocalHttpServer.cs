using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Memo.Core.Tests;

/// <summary>
/// Serveur HTTP local minimal pour les tests de téléchargement. Il n'utilise que la boucle locale,
/// ne contacte aucun réseau réel et implémente les réponses 200, 206 (Range), 302, 404 et 416.
/// </summary>
public sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Dictionary<string, RouteState> _routes = new(StringComparer.Ordinal);
    private readonly object _routesGate = new();

    public LocalHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}";
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl { get; }

    public Uri Url(string path) => new(BaseUrl + path);

    public void MapFile(string path, byte[] content)
    {
        var route = GetOrAdd(path);
        lock (route.Gate)
        {
            route.Content = content;
            route.Status = 200;
            route.Location = null;
            route.FailAfterBytes = -1;
        }
    }

    public void MapStatus(string path, int status, string? location = null)
    {
        var route = GetOrAdd(path);
        lock (route.Gate)
        {
            route.Content = Array.Empty<byte>();
            route.Status = status;
            route.Location = location;
            route.FailAfterBytes = -1;
        }
    }

    /// <summary>
    /// Sert le contenu normalement, sauf la première requête qui annonce la taille complète puis
    /// ferme la connexion après <paramref name="failAfterBytes"/> octets. La requête suivante avec
    /// <c>Range</c> reprend au bon offset.
    /// </summary>
    public void MapFlaky(string path, byte[] content, int failAfterBytes)
    {
        var route = GetOrAdd(path);
        lock (route.Gate)
        {
            route.Content = content;
            route.Status = 200;
            route.Location = null;
            route.FailAfterBytes = failAfterBytes;
            route.Failed = 0;
        }
    }

    public int RequestCount(string path)
    {
        var route = GetOrAdd(path);
        lock (route.Gate)
        {
            return route.Requests;
        }
    }

    public IReadOnlyList<string?> RangeHeaders(string path)
    {
        var route = GetOrAdd(path);
        lock (route.Gate)
        {
            return route.RangeHeaders.ToArray();
        }
    }

    private RouteState GetOrAdd(string path)
    {
        lock (_routesGate)
        {
            if (!_routes.TryGetValue(path, out var route))
            {
                route = new RouteState();
                _routes.Add(path, route);
            }

            return route;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            HttpRequest? request;
            try
            {
                request = await ReadRequestAsync(stream, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (request is null)
            {
                return;
            }

            RouteState? route;
            lock (_routesGate)
            {
                _routes.TryGetValue(request.Path, out route);
            }

            if (route is null)
            {
                await WriteSimpleAsync(stream, 404, "Not Found", Array.Empty<byte>(), _cts.Token)
                    .ConfigureAwait(false);
                return;
            }

            bool failOnce;
            string? rangeHeader;
            lock (route.Gate)
            {
                route.Requests++;
                route.RangeHeaders.Add(request.Range);
                failOnce = route.FailAfterBytes >= 0 && route.Failed == 0;
                if (failOnce)
                {
                    route.Failed = 1;
                }

                rangeHeader = request.Range;
            }

            if (route.Location is not null)
            {
                await WriteResponseAsync(
                        stream,
                        route.Status,
                        "Redirect",
                        Array.Empty<byte>(),
                        0,
                        null,
                        route.Location,
                        _cts.Token)
                    .ConfigureAwait(false);
                return;
            }

            if (route.Status != 200)
            {
                await WriteSimpleAsync(stream, route.Status, Reason(route.Status), Array.Empty<byte>(), _cts.Token)
                    .ConfigureAwait(false);
                return;
            }

            var total = route.Content.LongLength;
            long start = 0;
            var end = total - 1;
            var partial = false;
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                var parsed = ParseRange(rangeHeader, total);
                if (parsed is null)
                {
                    await WriteResponseAsync(
                            stream,
                            416,
                            "Range Not Satisfiable",
                            Array.Empty<byte>(),
                            0,
                            $"bytes */{total.ToString(CultureInfo.InvariantCulture)}",
                            null,
                            _cts.Token)
                        .ConfigureAwait(false);
                    return;
                }

                (start, end) = parsed.Value;
                partial = true;
            }

            var length = end - start + 1;
            if (length <= 0)
            {
                await WriteSimpleAsync(stream, 416, "Range Not Satisfiable", Array.Empty<byte>(), _cts.Token)
                    .ConfigureAwait(false);
                return;
            }

            if (failOnce)
            {
                var writable = (int)Math.Max(1, Math.Min(length / 2, int.MaxValue));
                var partialBody = new byte[writable];
                Array.Copy(route.Content, start, partialBody, 0, writable);
                await WriteResponseAsync(
                        stream,
                        partial ? 206 : 200,
                        partial ? "Partial Content" : "OK",
                        partialBody,
                        length,
                        partial
                            ? $"bytes {start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}"
                            : null,
                        null,
                        _cts.Token)
                    .ConfigureAwait(false);
                return;
            }

            var body = new byte[length];
            Array.Copy(route.Content, start, body, 0, length);
            await WriteResponseAsync(
                    stream,
                    partial ? 206 : 200,
                    partial ? "Partial Content" : "OK",
                    body,
                    length,
                    partial
                        ? $"bytes {start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}"
                        : null,
                    null,
                    _cts.Token)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteSimpleAsync(
        NetworkStream stream,
        int status,
        string reason,
        byte[] body,
        CancellationToken ct) =>
        await WriteResponseAsync(stream, status, reason, body, body.Length, null, null, ct)
            .ConfigureAwait(false);

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string reason,
        byte[] body,
        long declaredLength,
        string? contentRange,
        string? location,
        CancellationToken ct)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ")
            .Append(status.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(reason)
            .Append("\r\n");
        builder.Append("Content-Length: ")
            .Append(declaredLength.ToString(CultureInfo.InvariantCulture))
            .Append("\r\n");
        builder.Append("Content-Type: application/octet-stream\r\n");
        builder.Append("Accept-Ranges: bytes\r\n");
        if (contentRange is not null)
        {
            builder.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        }

        if (location is not null)
        {
            builder.Append("Location: ").Append(location).Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static (long Start, long End)? ParseRange(string? header, long length)
    {
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        if (!long.TryParse(
                spec[..dash],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var start))
        {
            return null;
        }

        var end = length - 1;
        var endText = spec[(dash + 1)..];
        if (endText.Length > 0)
        {
            if (!long.TryParse(
                    endText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedEnd))
            {
                return null;
            }

            end = Math.Min(parsedEnd, length - 1);
        }

        if (start < 0 || start >= length || end < start)
        {
            return null;
        }

        return (start, end);
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            length += read;
            var headerEnd = FindHeaderEnd(buffer, length);
            if (headerEnd < 0)
            {
                continue;
            }

            var text = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            var lines = text.Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return null;
            }

            var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var target = parts[1];
            var query = target.IndexOf('?');
            var path = query >= 0 ? target[..query] : target;
            string? range = null;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var name = line[..colon].Trim();
                if (string.Equals(name, "Range", StringComparison.OrdinalIgnoreCase))
                {
                    range = line[(colon + 1)..].Trim();
                }
            }

            return new HttpRequest(path, range);
        }

        return null;
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var index = 0; index + 3 < length; index++)
        {
            if (buffer[index] == '\r' &&
                buffer[index + 1] == '\n' &&
                buffer[index + 2] == '\r' &&
                buffer[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static string Reason(int status) => status switch
    {
        404 => "Not Found",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _cts.Dispose();
    }

    private sealed record HttpRequest(string Path, string? Range);

    private sealed class RouteState
    {
        public byte[] Content = Array.Empty<byte>();
        public int Status = 200;
        public string? Location;
        public int FailAfterBytes = -1;
        public int Failed;
        public int Requests;
        public readonly List<string?> RangeHeaders = new();
        public readonly object Gate = new();
    }
}
