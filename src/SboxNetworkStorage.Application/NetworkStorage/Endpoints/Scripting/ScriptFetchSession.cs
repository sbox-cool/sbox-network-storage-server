using System.Text;
using System.Text.Json;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>A fetch call that failed before any network I/O, or a completed call that failed. The host turns it into a catchable script error carrying <see cref="Code"/>.</summary>
public sealed class ScriptFetchException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A refused outbound call: the host and the block reason, for the owner's diagnostics.</summary>
public sealed record EndpointFetchBlockedCall(string Host, string Reason);

/// <summary>One fetch call for the endpoint trace. <see cref="ErrorCode"/> is null for a call that succeeded.</summary>
public sealed record ScriptFetchTraceEntry(int Ordinal, string Host, string Method, int? Status, string? ErrorCode, string? Reason);

/// <summary>
/// Per-execution state of <c>ns.fetch</c>, shared by every round of one request. The script calls
/// <see cref="Call"/> synchronously. A valid call that has no result yet is queued and returns null. The
/// executor then calls <see cref="LoadPendingAsync"/>, and the handler re-runs and finds the stored result. A
/// call is identified by its ordinal within the run. Within one pass, a call built from an earlier call's still-null
/// result may change between rounds and is sent again. An ordinal executed by an earlier pass may only replay the same
/// request; changed arguments are refused rather than issuing another external side effect from a discarded pass.
/// </summary>
public sealed class ScriptFetchSession(EndpointFetchPolicy policy, IScriptFetchTransport transport, bool captureTrace = false)
{
    /// <summary>Outbound requests one execution may make. Refused and cached calls do not count.</summary>
    public const int MaxRequestsPerExecution = 5;

    /// <summary>Largest request body a script may send.</summary>
    public const int MaxRequestBodyBytes = 64 * 1024;

    /// <summary>Largest response body a script may read.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    public const int DefaultTimeoutMs = 5000;
    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 10000;

    private const int MaxUrlLength = 2048;
    private const int MaxHeaders = 32;
    private const int MaxHeaderValueLength = 4096;
    private const int MaxBlockedCalls = 16;
    private const int MaxTraceEntries = 64;
    private const string TokenSeparators = "()<>@,;:\\\"/[]?={} \t";

    private static readonly HashSet<string> SupportedMethods = new(StringComparer.Ordinal) { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE" };
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "transfer-encoding", "connection", "upgrade", "expect", "te", "trailer", "keep-alive",
    };

    private static readonly string LimitMessage = $"ns.fetch: one execution may make at most {MaxRequestsPerExecution} outbound requests.";

    private readonly Dictionary<int, (string Key, ScriptFetchResult Result, int Pass)> _completed = [];
    private readonly List<PendingCall> _pending = [];
    private readonly SortedDictionary<int, ScriptFetchTraceEntry>? _trace = captureTrace ? new() : null;
    private readonly List<EndpointFetchBlockedCall> _blocked = [];
    private int _ordinal;
    private int _pass;
    private int _sent;

    private sealed record PendingCall(string Key, int Ordinal, string Host, ScriptFetchRequest Request);

    /// <summary>Calls queued by the current run that still need a network result.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Distinct refused calls (host and reason). Only live calls reach the owner's diagnostics.</summary>
    public IReadOnlyList<EndpointFetchBlockedCall> Blocked => _blocked;

    /// <summary>Fetch calls in ordinal order, for the endpoint trace. The first 64 calls are kept.</summary>
    public IReadOnlyList<ScriptFetchTraceEntry> Trace => _trace?.Values.ToList() ?? [];

    /// <summary>Starts a new pass of the handler. Calls executed by an earlier pass may only replay unchanged.</summary>
    public void BeginPass() => _pass++;

    /// <summary>Starts a new run. Only this round's resolved calls belong in its diagnostics.</summary>
    public void BeginRound()
    {
        _ordinal = 0;
        _trace?.Clear();
        _blocked.Clear();
    }

    /// <summary>Discards calls built before the current round's state was resolved; they must never be sent.</summary>
    public void DiscardPending()
    {
        _trace?.Clear();
        _pending.Clear();
        _blocked.Clear();
    }

    /// <summary>The script-facing <c>ns.fetch(url, options)</c>. Returns the response, or null while the result is loading.</summary>
    public object? Call(string url, JsonElement options)
    {
        var ordinal = ++_ordinal;
        PendingCall call;
        try
        {
            call = Parse(ordinal, url, options);
        }
        catch (ScriptFetchException error)
        {
            SetTrace(ordinal, string.Empty, string.Empty, null, error.Code, null);
            throw;
        }

        if (_completed.TryGetValue(ordinal, out var done))
        {
            if (string.Equals(done.Key, call.Key, StringComparison.Ordinal))
            {
                SetCompletedTrace(call, done.Result);
                return Deliver(done.Result);
            }
            if (done.Pass != _pass)
            {
                SetTrace(ordinal, call.Host, call.Request.Method, null, "FETCH_REPLAY_CONFLICT", "request_changed");
                throw new ScriptFetchException("FETCH_REPLAY_CONFLICT",
                    "ns.fetch: an executed call changed during a script rerun; no second request was sent. An earlier external effect may already have completed.");
            }
            // Same pass: the call was built from a result that was still loading, so it is sent again below.
        }

        if (FetchHostRules.TryGetIpLiteral(call.Host, out var literal)
            && FetchAddressRules.BlockedReason(literal, transport.AllowPrivateNetworks) is { } addressReason)
            throw Refuse(call, addressReason);

        if (!policy.AllowAllPublic && !FetchHostRules.IsAllowed(call.Host, policy.AllowedHosts))
            throw Refuse(call, "host_not_allowed");

        if (_sent + _pending.Count >= MaxRequestsPerExecution)
        {
            SetTrace(ordinal, call.Host, call.Request.Method, null, "FETCH_LIMIT_EXCEEDED", null);
            throw new ScriptFetchException("FETCH_LIMIT_EXCEEDED", LimitMessage);
        }

        _pending.Add(call);
        SetTrace(ordinal, call.Host, call.Request.Method, null, null, null);
        return null;
    }

    /// <summary>Sends every queued call and stores each result. Only cancellation throws.</summary>
    public async Task LoadPendingAsync(CancellationToken ct)
    {
        if (_pending.Count == 0)
            return;

        var calls = _pending.ToArray();
        _pending.Clear();
        _sent += calls.Length;
        var results = await Task.WhenAll(calls.Select(call => transport.SendAsync(call.Request, policy, ct)));

        for (var i = 0; i < calls.Length; i++)
        {
            var call = calls[i];
            var result = results[i];
            _completed[call.Ordinal] = (call.Key, result, _pass);
            SetCompletedTrace(call, result);
        }
    }

    private object? Deliver(ScriptFetchResult result)
    {
        if (result.Response is { } response)
            return ResponseValue(response);

        var failure = result.Failure!;
        throw new ScriptFetchException(failure.Code, FailureMessage(failure));
    }

    private ScriptFetchException Refuse(PendingCall call, string reason)
    {
        RecordBlocked(call.Host, reason);
        SetTrace(call.Ordinal, call.Host, call.Request.Method, null, "FETCH_BLOCKED", reason);
        return new ScriptFetchException("FETCH_BLOCKED", BlockedMessage(call.Host, reason));
    }

    private void RecordBlocked(string host, string reason)
    {
        if (_blocked.Count < MaxBlockedCalls && !_blocked.Any(entry => entry.Host == host && entry.Reason == reason))
            _blocked.Add(new EndpointFetchBlockedCall(host, reason));
    }

    private void SetCompletedTrace(PendingCall call, ScriptFetchResult result)
    {
        if (result.Failure is { } failure)
        {
            if (failure.Code == "FETCH_BLOCKED")
                RecordBlocked(failure.Host, failure.Reason);
            SetTrace(call.Ordinal, failure.Host, call.Request.Method, null, failure.Code, failure.Reason);
        }
        else
        {
            SetTrace(call.Ordinal, call.Host, call.Request.Method, result.Response!.Status, null, null);
        }
    }

    private void SetTrace(int ordinal, string host, string method, int? status, string? error, string? reason)
    {
        if (_trace is not null && (_trace.ContainsKey(ordinal) || _trace.Count < MaxTraceEntries))
            _trace[ordinal] = new ScriptFetchTraceEntry(ordinal, host, method, status, error, reason);
    }

    private static PendingCall Parse(int ordinal, string url, JsonElement options)
    {
        if (url.Length > MaxUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || uri.Host.Length == 0)
        {
            throw Invalid("the URL must be an absolute http or https URL without credentials.");
        }

        var method = "GET";
        var headers = new List<KeyValuePair<string, string>>();
        (byte[] Bytes, bool Json)? body = null;
        var timeoutMs = DefaultTimeoutMs;

        if (options.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (options.ValueKind is not JsonValueKind.Object)
                throw Invalid("options must be an object.");

            foreach (var property in options.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "method":
                        if (property.Value.ValueKind is not JsonValueKind.String)
                            throw Invalid("method must be a string.");
                        method = property.Value.GetString()!.ToUpperInvariant();
                        break;
                    case "headers":
                        headers = ParseHeaders(property.Value);
                        break;
                    case "body":
                        body = ParseBody(property.Value);
                        break;
                    case "timeoutMs":
                        timeoutMs = ParseTimeout(property.Value);
                        break;
                    default:
                        throw Invalid($"the option \"{property.Name}\" is not supported.");
                }
            }
        }

        if (!SupportedMethods.Contains(method))
            throw Invalid("method must be GET, HEAD, POST, PUT, PATCH or DELETE.");
        if (body is not null && (method is "GET" or "HEAD"))
            throw Invalid($"a {method} request cannot have a body.");

        if (body is { Json: true } && !headers.Any(header => string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase)))
            headers.Add(new KeyValuePair<string, string>("content-type", "application/json"));

        var host = FetchHostRules.NormalizeHost(uri.Host);
        var bodyBytes = body?.Bytes;
        var headerKey = string.Join("\u0001", headers.Select(header => $"{header.Key}:{header.Value}"));
        var bodyKey = bodyBytes is null ? string.Empty : Convert.ToBase64String(bodyBytes);
        var key = $"{ordinal}\n{method}\n{uri.AbsoluteUri}\n{timeoutMs}\n{headerKey}\n{bodyKey}";

        return new PendingCall(
            key,
            ordinal,
            host,
            new ScriptFetchRequest(uri.AbsoluteUri, method, headers, bodyBytes, timeoutMs));
    }

    private static List<KeyValuePair<string, string>> ParseHeaders(JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Object)
            throw Invalid("headers must be an object of strings.");

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var property in value.EnumerateObject())
        {
            var name = property.Name;
            if (property.Value.ValueKind is not JsonValueKind.String)
                throw Invalid($"the header \"{name}\" must be a string.");
            if (!IsToken(name) || ForbiddenHeaders.Contains(name) || name.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase))
                throw Invalid($"the header \"{name}\" cannot be sent.");

            var text = property.Value.GetString()!;
            if (text.Length > MaxHeaderValueLength || text.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                throw Invalid($"the header \"{name}\" is not a valid value.");

            headers.Add(new KeyValuePair<string, string>(name, text));
            if (headers.Count > MaxHeaders)
                throw Invalid($"at most {MaxHeaders} headers are allowed.");
        }
        return headers;
    }

    private static (byte[] Bytes, bool Json) ParseBody(JsonElement value)
    {
        byte[] bytes;
        bool json;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                bytes = Encoding.UTF8.GetBytes(value.GetString()!);
                json = false;
                break;
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                bytes = Encoding.UTF8.GetBytes(value.GetRawText());
                json = true;
                break;
            default:
                throw Invalid("body must be a string, an object or an array.");
        }

        if (bytes.Length > MaxRequestBodyBytes)
            throw Invalid($"the body is larger than {MaxRequestBodyBytes / 1024} KiB.");
        return (bytes, json);
    }

    private static int ParseTimeout(JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Number || !value.TryGetDouble(out var milliseconds))
            throw Invalid("timeoutMs must be a number.");
        return (int)Math.Clamp(Math.Round(milliseconds), MinTimeoutMs, MaxTimeoutMs);
    }

    private static bool IsToken(string name) =>
        name.Length > 0 && name.All(c => c > ' ' && c < 127 && TokenSeparators.IndexOf(c) < 0);

    private static ScriptFetchException Invalid(string detail) =>
        new("FETCH_INVALID_REQUEST", $"ns.fetch: {detail}");

    private static string FailureMessage(ScriptFetchFailure failure) => failure.Code switch
    {
        "FETCH_BLOCKED" => BlockedMessage(failure.Host, failure.Reason),
        "FETCH_TIMEOUT" => $"ns.fetch: the request to \"{failure.Host}\" timed out.",
        "FETCH_RESPONSE_TOO_LARGE" => $"ns.fetch: the response from \"{failure.Host}\" is larger than {MaxResponseBytes / 1024} KiB.",
        "FETCH_LIMIT_EXCEEDED" => LimitMessage,
        _ => $"ns.fetch: the request to \"{failure.Host}\" failed.",
    };

    private static string BlockedMessage(string host, string reason) => reason switch
    {
        "host_not_allowed" => $"ns.fetch: \"{host}\" is not on this project's outbound allow-list.",
        FetchAddressRules.MetadataAddress => $"ns.fetch: \"{host}\" is a cloud metadata address, which is always blocked.",
        FetchAddressRules.ReservedAddress => $"ns.fetch: \"{host}\" resolves to a reserved address, which is blocked.",
        _ => $"ns.fetch: \"{host}\" resolves to a private network address, which is blocked.",
    };

    private static Dictionary<string, object?> ResponseValue(ScriptFetchResponse response)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in response.Headers)
            headers[name] = value;

        object? json = null;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            json = ScriptJson.ToPlain(document.RootElement);
        }
        catch (JsonException)
        {
            json = null;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = (long)response.Status,
            ["ok"] = response.Status is >= 200 and < 300,
            ["headers"] = headers,
            ["body"] = response.Body,
            ["json"] = json,
        };
    }
}
