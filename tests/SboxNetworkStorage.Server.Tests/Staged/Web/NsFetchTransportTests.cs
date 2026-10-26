using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Drives <see cref="HttpScriptFetchTransport"/> over real loopback sockets. DNS and connect are replaced by
/// <see cref="FakeNetwork"/>: names resolve from a table (anything else is public), and every connect is recorded
/// and then lands on a local <see cref="RawResponder"/>, whatever address the transport asked for.
/// </summary>
public sealed class NsFetchTransportTests
{
    private const string PublicAddress = "93.184.216.34";

    // Matches ScriptFetchSession.MaxResponseBytes (256 KiB).
    private const int ResponseCapBytes = 256 * 1024;

    private static readonly EndpointFetchPolicy AllowAllPublicPolicy = new([], true);

    [Fact]
    public async Task SendAsync_Returns200WithLowercaseHeadersAndUtf8Body()
    {
        var body = Encoding.UTF8.GetBytes("{\"name\":\"Zoë\",\"ok\":true}");
        await using var responder = new RawResponder(Reply(Raw(
            "HTTP/1.1 200 OK",
            "Content-Type: application/json; charset=utf-8\r\nX-Trace: a\r\nX-Trace: b\r\n" +
            $"Content-Length: {body.Length}\r\n",
            body)));
        var network = new FakeNetwork(responder.Port);
        using var transport = CreateTransport(network);

        var response = ExpectResponse(await Fetch(transport, "http://api.example.test/data"));

        Assert.Equal(200, response.Status);
        Assert.Equal("application/json; charset=utf-8", response.Headers["content-type"]);
        Assert.Equal("a, b", response.Headers["x-trace"]);
        Assert.False(response.Headers.ContainsKey("X-Trace"));
        Assert.Equal("{\"name\":\"Zoë\",\"ok\":true}", response.Body);
        Assert.Equal("GET /data HTTP/1.1", Assert.Single(responder.Requests));
        Assert.Equal(PublicAddress, Assert.Single(network.Connected));
    }

    [Fact]
    public async Task SendAsync_BlocksRedirectToPrivateLiteralWithoutSecondRequest()
    {
        await using var responder = new RawResponder(Reply(Redirect("http://10.0.0.5/x")));
        var network = new FakeNetwork(responder.Port);
        using var transport = CreateTransport(network);

        var failure = ExpectFailure(await Fetch(transport, "http://api.example.test/start"));

        Assert.Equal("FETCH_BLOCKED", failure.Code);
        Assert.Equal("private_address", failure.Reason);
        Assert.Equal("10.0.0.5", failure.Host);
        // Only the first hop reached the responder and the socket layer.
        Assert.Single(responder.Requests);
        Assert.Equal(PublicAddress, Assert.Single(network.Connected));
    }

    [Fact]
    public async Task SendAsync_BlocksRedirectToNameThatResolvesToPrivateAddressBeforeConnecting()
    {
        await using var responder = new RawResponder(Reply(Redirect("http://intranet.example.test/x")));
        var network = new FakeNetwork(
            responder.Port,
            new Dictionary<string, string> { ["intranet.example.test"] = "10.0.0.5" });
        using var transport = CreateTransport(network);

        var failure = ExpectFailure(await Fetch(transport, "http://api.example.test/start"));

        Assert.Equal("FETCH_BLOCKED", failure.Code);
        Assert.Equal("private_address", failure.Reason);
        Assert.Equal("intranet.example.test", failure.Host);
        Assert.Contains("intranet.example.test", network.Resolved);
        Assert.Single(responder.Requests);
        // The blocked name never reached connect: only the public first hop did.
        Assert.Equal(PublicAddress, Assert.Single(network.Connected));
    }

    [Fact]
    public async Task SendAsync_BlocksRedirectToMetadataAddressEvenWhenPrivateNetworksAllowed()
    {
        await using var responder = new RawResponder(Reply(Redirect("http://169.254.169.254/")));
        var network = new FakeNetwork(responder.Port);
        using var transport = CreateTransport(network, allowPrivateNetworks: true);

        var failure = ExpectFailure(await Fetch(transport, "http://api.example.test/start"));

        Assert.Equal("FETCH_BLOCKED", failure.Code);
        Assert.Equal("metadata_address", failure.Reason);
        Assert.Equal("169.254.169.254", failure.Host);
        Assert.Single(responder.Requests);
        Assert.Equal(PublicAddress, Assert.Single(network.Connected));
    }

    [Fact]
    public async Task SendAsync_FailsWithTooManyRedirectsAfterFiveFollowedHops()
    {
        // Every hop redirects to the same URL, so the chain never ends. Five redirects are followed and the sixth
        // redirect is refused, which makes six requests in total.
        await using var responder = new RawResponder(Reply(Redirect("http://api.example.test/loop")));
        var network = new FakeNetwork(responder.Port);
        using var transport = CreateTransport(network);

        var failure = ExpectFailure(await Fetch(transport, "http://api.example.test/start"));

        Assert.Equal("FETCH_FAILED", failure.Code);
        Assert.Equal("too_many_redirects", failure.Reason);
        Assert.Equal("api.example.test", failure.Host);
        Assert.Equal(6, responder.Requests.Count);
        Assert.Equal(6, network.Connected.Count);
    }

    [Fact]
    public async Task SendAsync_FailsWhenContentLengthExceedsResponseCap()
    {
        var size = ResponseCapBytes + 1;
        await using var responder = new RawResponder(Reply(Raw(
            "HTTP/1.1 200 OK",
            $"Content-Length: {size}\r\n",
            new byte[size])));
        var network = new FakeNetwork(responder.Port);
        using var transport = CreateTransport(network);

        var failure = ExpectFailure(await Fetch(transport, "http://api.example.test/big"));

        Assert.Equal("FETCH_RESPONSE_TOO_LARGE", failure.Code);
        Assert.Equal("response_too_large", failure.Reason);
        Assert.Equal("api.example.test", failure.Host);
    }

    [Fact]
    public async Task SendAsync_FailsWhenChunkedBodyOverflowsResponseCap()
    {
        // No Content-Length, so the cap is enforced while reading. Five 64 KiB chunks total 320 KiB.
        await using var responder = new RawResponder(Reply(Raw(
            "HTTP/1.1 200 OK",
            "Transfer-Encoding: chunked\r\n",
            ChunkedBody(chunks: 5, chunkSize: 64 * 1024))));
        var network = new FakeNetwork(responder.Port);
        using var transport = CreateTransport(network);

        var failure = ExpectFailure(await Fetch(transport, "http://api.example.test/stream"));

        Assert.Equal("FETCH_RESPONSE_TOO_LARGE", failure.Code);
        Assert.Equal("response_too_large", failure.Reason);
        Assert.Equal("api.example.test", failure.Host);
    }

    [Fact]
    public async Task SendAsync_ReturnsTimeoutWhenResponderNeverAnswers()
    {
        await using var responder = new RawResponder(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        var network = new FakeNetwork(responder.Port);
        using var transport = CreateTransport(network);

        var failure = ExpectFailure(await Fetch(transport, "http://api.example.test/slow", timeoutMs: 100));

        Assert.Equal("FETCH_TIMEOUT", failure.Code);
        Assert.Equal("timeout", failure.Reason);
        Assert.Equal("api.example.test", failure.Host);
    }

    [Fact]
    public async Task SendAsync_FailsWithNetworkErrorWhenConnectionIsRefused()
    {
        var network = new FakeNetwork(0);
        // Refusal is simulated, not provoked: a closed loopback port is not refused reliably on every host.
        using var transport = new HttpScriptFetchTransport(false, network.ResolveAsync, (address, _, _) =>
        {
            network.Connected.Add(address.ToString());
            return Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused));
        });

        var failure = ExpectFailure(await Fetch(transport, "http://api.example.test/refused"));

        Assert.Equal("FETCH_FAILED", failure.Code);
        Assert.Equal("network_error", failure.Reason);
        Assert.Equal("api.example.test", failure.Host);
        Assert.Equal(PublicAddress, Assert.Single(network.Connected));
    }

    [Fact]
    public async Task SendAsync_FailsWithNetworkErrorWhenResponderClosesWithoutAnswering()
    {
        await using var responder = new RawResponder((_, _) => Task.FromResult<byte[]?>(null));
        var network = new FakeNetwork(responder.Port);
        using var transport = CreateTransport(network);

        var failure = ExpectFailure(await Fetch(transport, "http://api.example.test/silent"));

        Assert.Equal("FETCH_FAILED", failure.Code);
        Assert.Equal("network_error", failure.Reason);
        Assert.Equal("api.example.test", failure.Host);
    }

    [Fact]
    public async Task SendAsync_BlocksNameResolvingToPrivateAddressWhenPrivateNetworksDisallowed()
    {
        await using var responder = new RawResponder(Reply(OkText("internal")));
        var network = new FakeNetwork(
            responder.Port,
            new Dictionary<string, string> { ["intranet.example.test"] = "10.0.0.5" });
        using var transport = CreateTransport(network, allowPrivateNetworks: false);

        var failure = ExpectFailure(await Fetch(transport, "http://intranet.example.test/"));

        Assert.Equal("FETCH_BLOCKED", failure.Code);
        Assert.Equal("private_address", failure.Reason);
        Assert.Equal("intranet.example.test", failure.Host);
        Assert.Empty(network.Connected);
        Assert.Empty(responder.Requests);
    }

    [Fact]
    public async Task SendAsync_ConnectsToNameResolvingToPrivateAddressWhenPrivateNetworksAllowed()
    {
        await using var responder = new RawResponder(Reply(OkText("internal")));
        var network = new FakeNetwork(
            responder.Port,
            new Dictionary<string, string> { ["intranet.example.test"] = "10.0.0.5" });
        using var transport = CreateTransport(network, allowPrivateNetworks: true);

        var response = ExpectResponse(await Fetch(transport, "http://intranet.example.test/"));

        Assert.Equal(200, response.Status);
        Assert.Equal("internal", response.Body);
        Assert.Equal("10.0.0.5", Assert.Single(network.Connected));
    }

    [Fact]
    public async Task SendAsync_BlocksNameResolvingToMetadataAddressWhenPrivateNetworksAllowed()
    {
        await using var responder = new RawResponder(Reply(OkText("leaked")));
        var network = new FakeNetwork(
            responder.Port,
            new Dictionary<string, string> { ["metadata.example.test"] = "169.254.169.254" });
        using var transport = CreateTransport(network, allowPrivateNetworks: true);

        var failure = ExpectFailure(await Fetch(transport, "http://metadata.example.test/latest/meta-data/"));

        Assert.Equal("FETCH_BLOCKED", failure.Code);
        Assert.Equal("metadata_address", failure.Reason);
        Assert.Equal("metadata.example.test", failure.Host);
        Assert.Empty(network.Connected);
        Assert.Empty(responder.Requests);
    }

    private static HttpScriptFetchTransport CreateTransport(FakeNetwork network, bool allowPrivateNetworks = false) =>
        new(allowPrivateNetworks, network.ResolveAsync, network.ConnectAsync);

    // Security and response-shape checks must not race the shared runner's scheduler.
    // Timeout-specific cases pass their own short deadline explicitly.
    private static Task<ScriptFetchResult> Fetch(HttpScriptFetchTransport transport, string url, int timeoutMs = 30000) =>
        transport.SendAsync(
            new ScriptFetchRequest(url, "GET", [], null, timeoutMs),
            AllowAllPublicPolicy,
            CancellationToken.None);

    private static ScriptFetchResponse ExpectResponse(ScriptFetchResult result)
    {
        Assert.Null(result.Failure);
        Assert.NotNull(result.Response);
        return result.Response!;
    }

    private static ScriptFetchFailure ExpectFailure(ScriptFetchResult result)
    {
        Assert.Null(result.Response);
        Assert.NotNull(result.Failure);
        return result.Failure!;
    }

    private static Func<string, CancellationToken, Task<byte[]?>> Reply(byte[] response) =>
        (_, _) => Task.FromResult<byte[]?>(response);

    /// <summary>Builds a raw HTTP/1.1 response. Every response says Connection: close, so each request gets its own socket.</summary>
    private static byte[] Raw(string statusLine, string headers, byte[]? body = null)
    {
        var head = Encoding.ASCII.GetBytes($"{statusLine}\r\n{headers}Connection: close\r\n\r\n");
        if (body is null)
            return head;
        return [.. head, .. body];
    }

    private static byte[] Redirect(string location) =>
        Raw("HTTP/1.1 302 Found", $"Location: {location}\r\nContent-Length: 0\r\n");

    private static byte[] OkText(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        return Raw("HTTP/1.1 200 OK", $"Content-Type: text/plain\r\nContent-Length: {body.Length}\r\n", body);
    }

    /// <summary>Chunk-encodes <paramref name="chunks"/> blocks of zero bytes, then the last-chunk.</summary>
    private static byte[] ChunkedBody(int chunks, int chunkSize)
    {
        using var body = new MemoryStream();
        var sizeLine = Encoding.ASCII.GetBytes($"{chunkSize:X}\r\n");
        var data = new byte[chunkSize];
        for (var i = 0; i < chunks; i++)
        {
            body.Write(sizeLine, 0, sizeLine.Length);
            body.Write(data, 0, data.Length);
            body.Write("\r\n"u8);
        }
        body.Write("0\r\n\r\n"u8);
        return body.ToArray();
    }

    /// <summary>
    /// Stands in for DNS and the socket layer. Names resolve from <paramref name="addresses"/>; anything else is
    /// public. Each connect is recorded with the address the transport asked for, then goes to the local responder.
    /// </summary>
    private sealed class FakeNetwork(int port, IReadOnlyDictionary<string, string>? addresses = null)
    {
        public List<string> Resolved { get; } = [];

        public List<string> Connected { get; } = [];

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            Resolved.Add(host);
            var address = addresses is not null && addresses.TryGetValue(host, out var text) ? text : PublicAddress;
            return Task.FromResult(new[] { IPAddress.Parse(address) });
        }

        public async Task<Stream> ConnectAsync(IPAddress address, int requestedPort, CancellationToken ct)
        {
            Connected.Add(address.ToString());
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Loopback HTTP/1.1 server. Each connection serves one request: it reads the request head, records the request
    /// line, and writes whatever the script returns. A null response closes the socket without answering.
    /// </summary>
    private sealed class RawResponder : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<string, CancellationToken, Task<byte[]?>> _script;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _requests = new();

        public RawResponder(Func<string, CancellationToken, Task<byte[]?>> script)
        {
            _script = script;
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Request lines received so far, in arrival order.</summary>
        public ConcurrentQueue<string> Requests => _requests;

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The listener was stopped: the test is over.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    var requestLine = await ReadRequestLineAsync(stream, _stop.Token) ?? string.Empty;
                    _requests.Enqueue(requestLine);

                    var response = await _script(requestLine, _stop.Token);
                    if (response is not null)
                        await stream.WriteAsync(response, _stop.Token);
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // The client gave up (timeout, cancellation, closed socket) or the test ended.
                }
            }
        }

        private static async Task<string?> ReadRequestLineAsync(Stream stream, CancellationToken ct)
        {
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);

            var requestLine = await reader.ReadLineAsync(ct);
            string? line;
            do
            {
                line = await reader.ReadLineAsync(ct);
            } while (line is { Length: > 0 });
            return requestLine;
        }
    }
}
