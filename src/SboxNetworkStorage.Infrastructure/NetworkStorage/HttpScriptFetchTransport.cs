using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// <see cref="IScriptFetchTransport"/> for <c>ns.fetch</c>. Address checks run where the socket is opened: every
/// name is resolved, any blocked address refuses the connection, and the socket connects only to an address that
/// passed <see cref="FetchAddressRules"/>. Redirects are followed by hand, so every hop repeats the allow-list and
/// address checks. A redirect cannot reach a blocked address or an unlisted host.
/// </summary>
public sealed class HttpScriptFetchTransport : IScriptFetchTransport, IDisposable
{
    private const int MaxRedirects = 5;
    private const int ReadBufferBytes = 8 * 1024;

    private static readonly HashSet<string> CrossOriginDroppedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "cookie", "proxy-authorization",
    };

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPAddress, int, CancellationToken, Task<Stream>> _connect;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _client;

    public HttpScriptFetchTransport(bool allowPrivateNetworks)
        : this(allowPrivateNetworks, Dns.GetHostAddressesAsync, ConnectSocketAsync)
    {
    }

    internal HttpScriptFetchTransport(
        bool allowPrivateNetworks,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, int, CancellationToken, Task<Stream>> connect)
    {
        AllowPrivateNetworks = allowPrivateNetworks;
        _resolve = resolve;
        _connect = connect;
        _handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool AllowPrivateNetworks { get; }

    public async Task<ScriptFetchResult> SendAsync(ScriptFetchRequest request, EndpointFetchPolicy policy, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(request.TimeoutMs);

        var current = new Uri(request.Url, UriKind.Absolute);
        var method = request.Method;
        var body = request.Body;
        var headers = request.Headers;
        var host = FetchHostRules.NormalizeHost(current.Host);
        var redirects = 0;

        try
        {
            while (true)
            {
                host = FetchHostRules.NormalizeHost(current.Host);
                if (RefusalReason(host, policy) is { } refusal)
                    return Fail("FETCH_BLOCKED", refusal, host);
                if (current.Scheme is not ("http" or "https"))
                    return Fail("FETCH_FAILED", "unsupported_scheme", host);

                using var message = BuildMessage(method, current, headers, body);
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var status = (int)response.StatusCode;

                if (IsRedirect(status) && response.Headers.Location is { } location)
                {
                    if (redirects == MaxRedirects)
                        return Fail("FETCH_FAILED", "too_many_redirects", host);

                    redirects++;
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    (method, body, headers) = RedirectRequest(status, method, body, headers, current, next);
                    current = next;
                    continue;
                }

                if (method == "HEAD")
                    return ScriptFetchResult.Ok(new ScriptFetchResponse(status, HeaderMap(response), string.Empty));

                if (response.Content.Headers.ContentLength > ScriptFetchSession.MaxResponseBytes)
                    return Fail("FETCH_RESPONSE_TOO_LARGE", "response_too_large", host);

                var bytes = await ReadCappedAsync(response.Content, deadline.Token);
                if (bytes is null)
                    return Fail("FETCH_RESPONSE_TOO_LARGE", "response_too_large", host);

                return ScriptFetchResult.Ok(new ScriptFetchResponse(status, HeaderMap(response), Encoding.UTF8.GetString(bytes)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (FindBlock(error) is { } block)
        {
            return Fail("FETCH_BLOCKED", block.Reason, block.Host);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Fail("FETCH_TIMEOUT", "timeout", host);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or SocketException or UriFormatException)
        {
            return Fail("FETCH_FAILED", "network_error", host);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    /// <summary>The allow-list and literal-address rules that apply before any hop is sent.</summary>
    private string? RefusalReason(string host, EndpointFetchPolicy policy)
    {
        if (FetchHostRules.TryGetIpLiteral(host, out var literal)
            && FetchAddressRules.BlockedReason(literal, AllowPrivateNetworks) is { } addressReason)
            return addressReason;

        if (!policy.AllowAllPublic && !FetchHostRules.IsAllowed(host, policy.AllowedHosts))
            return "host_not_allowed";

        return null;
    }

    /// <summary>Connection callback: resolves the name, refuses if any address is blocked, then connects.</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses;
        if (FetchHostRules.TryGetIpLiteral(host, out var literal))
            addresses = new[] { literal };
        else
            addresses = await _resolve(host, ct);

        if (addresses.Length == 0)
            throw new HttpRequestException("ns.fetch: the host did not resolve.");

        foreach (var address in addresses)
        {
            if (FetchAddressRules.BlockedReason(address, AllowPrivateNetworks) is { } reason)
                throw new FetchAddressBlockedException(FetchHostRules.NormalizeHost(host), reason);
        }

        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            try
            {
                return await _connect(address, port, ct);
            }
            catch (SocketException error)
            {
                lastError = error;
            }
        }

        throw new HttpRequestException("ns.fetch: the host refused the connection.", lastError);
    }

    private static (string Method, byte[]? Body, IReadOnlyList<KeyValuePair<string, string>> Headers) RedirectRequest(
        int status,
        string method,
        byte[]? body,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        Uri from,
        Uri to)
    {
        // Same rules as the fetch standard: 303 turns any non-GET/HEAD into GET, 301/302 turn POST into GET.
        var becomesGet = status == 303
            ? method is not ("GET" or "HEAD")
            : (status is 301 or 302) && method == "POST";

        if (becomesGet)
        {
            method = "GET";
            body = null;
            headers = headers.Where(header => !header.Key.StartsWith("content-", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!SameOrigin(from, to))
            headers = headers.Where(header => !CrossOriginDroppedHeaders.Contains(header.Key)).ToList();

        return (method, body, headers);
    }

    private static bool IsRedirect(int status) => status is 301 or 302 or 303 or 307 or 308;

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    private static HttpRequestMessage BuildMessage(
        string method,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        byte[]? body)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), uri);
        if (body is not null || method is "POST" or "PUT" or "PATCH")
            message.Content = new ByteArrayContent(body ?? []);

        foreach (var (name, value) in headers)
        {
            if (name.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
            {
                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    private static Dictionary<string, string> HeaderMap(HttpResponseMessage response)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            var name = header.Key.ToLowerInvariant();
            var value = string.Join(", ", header.Value);
            map[name] = map.TryGetValue(name, out var existing) ? $"{existing}, {value}" : value;
        }
        return map;
    }

    /// <summary>Reads the body up to the response cap. Returns null when the body is larger.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadBufferBytes];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), ct);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > ScriptFetchSession.MaxResponseBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
    }

    private static async Task<Stream> ConnectSocketAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static FetchAddressBlockedException? FindBlock(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is FetchAddressBlockedException blocked)
                return blocked;
        }
        return null;
    }

    private static ScriptFetchResult Fail(string code, string reason, string host) =>
        ScriptFetchResult.Fail(new ScriptFetchFailure(code, reason, host));

    /// <summary>Thrown from the connect callback when a resolved address is blocked. Carries the owner-facing reason.</summary>
    private sealed class FetchAddressBlockedException(string host, string reason)
        : Exception($"ns.fetch: {host} resolves to a blocked address ({reason}).")
    {
        public string Host { get; } = host;

        public string Reason { get; } = reason;
    }
}
