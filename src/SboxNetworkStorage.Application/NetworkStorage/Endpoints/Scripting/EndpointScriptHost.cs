using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// The language-neutral host surface a script sees as <c>ns</c>. Every <see cref="ScriptApiAttribute"/> method is
/// callable from any runtime; runtimes exchange only JSON text with it, so no host object crosses into script.
/// Calls arrive as a name plus a JSON array of arguments and return a JSON envelope
/// (<c>{"ok":true,"value":...}</c> or <c>{"ok":false,"error":"..."}</c>).
/// </summary>
public sealed class EndpointScriptHost
{
    private static readonly IReadOnlyDictionary<string, MethodInfo> Api = typeof(EndpointScriptHost)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Select(method => (Method: method, Attribute: method.GetCustomAttribute<ScriptApiAttribute>()))
        .Where(pair => pair.Attribute is not null)
        .ToDictionary(pair => pair.Attribute!.Name, pair => pair.Method, StringComparer.Ordinal);

    private readonly EndpointScriptBindings _bindings;
    private readonly int _maxLogBytes;
    private readonly List<string> _logs = [];
    private int _logBytes;
    private bool _logTruncated;
    private readonly ILogger? _logger;

    public EndpointScriptHost(EndpointScriptBindings bindings, int maxLogBytes, ILogger? logger = null)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _maxLogBytes = maxLogBytes;
        _logger = logger;
    }

    /// <summary>The names of every script API member, sorted. Runtimes generate their bindings from this list.</summary>
    public static IReadOnlyList<string> ApiNames { get; } = Api.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>The members scripts may call: every script API member except those marked Internal. The prelude builds <c>ns</c> from this list.</summary>
    public static IReadOnlyList<string> PublicApiNames { get; } = Api
        .Where(pair => !pair.Value.GetCustomAttribute<ScriptApiAttribute>()!.Internal)
        .Select(pair => pair.Key)
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>Set when the script called <c>ns.fail</c> or a storage call failed. Wins over whatever the script returns.</summary>
    public EndpointScriptFailureException? Failure { get; private set; }

    /// <summary>Log lines written by <c>ns.log</c>, capped at the runtime's log limit.</summary>
    public IReadOnlyList<string> Logs => _logs;

    /// <summary>Invokes one API member. Never throws: failures come back in the envelope.</summary>
    public string Invoke(string name, string argumentsJson)
    {
        if (!Api.TryGetValue(name, out var method))
            return Envelope(false, null, $"Unknown endpoint API '{name}'.");
        try
        {
            var arguments = JsonSerializer.Deserialize<JsonElement[]>(argumentsJson) ?? [];
            var parameters = method.GetParameters();
            var values = new object?[parameters.Length];
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                if (index < arguments.Length && arguments[index].ValueKind != JsonValueKind.Undefined)
                    values[index] = Convert(arguments[index], parameter.ParameterType, name, index);
                else if (parameter.HasDefaultValue)
                    values[index] = parameter.DefaultValue is null or DBNull
                        ? (parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null)
                        : parameter.DefaultValue;
                else
                    throw new ArgumentException($"{name} needs argument {index + 1}.");
            }
            var result = method.Invoke(this, values);
            return Envelope(true, result, null);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
        {
            return RecordFailure(wrapped.InnerException);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidCastException)
        {
            return Envelope(false, null, error.Message);
        }
    }

    private string RecordFailure(Exception error)
    {
        if (error is EndpointScriptFailureException failure)
        {
            Failure ??= failure;
            return Envelope(false, null, failure.Message);
        }
        if (error is ScriptFetchException fetch)
            return Envelope(false, null, fetch.Message, fetch.Code);
        if (error is ScriptLimitExceededException limit)
        {
            Failure ??= new EndpointScriptFailureException(500, "SCRIPT_LIMIT_EXCEEDED", "The endpoint exceeded a limit.", limit.Message);
            return Envelope(false, null, Failure.Message);
        }
        if (error is ArgumentException or JsonException or InvalidCastException)
            return Envelope(false, null, error.Message);
        // Unexpected host fault: the endpoint fails closed as a server error. The owner gets the type and message;
        // the full trace goes to the server log.
        _logger?.LogError(error, "Endpoint script host fault");
        Failure ??= new EndpointScriptFailureException(500, "SCRIPT_HOST_ERROR", "The endpoint could not complete this request.", DescribeForOwner(error));
        return Envelope(false, null, Failure.Message);
    }

    /// <summary>The owner-facing description of an unexpected exception: its type and message, never a stack trace.</summary>
    internal static string DescribeForOwner(Exception error) => $"{error.GetType().FullName}: {error.Message}";

    private static object? Convert(JsonElement element, Type type, string name, int index)
    {
        if (type == typeof(string))
            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : throw new ArgumentException($"{name}: argument {index + 1} must be a string.");
        if (type == typeof(int))
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var integer)
                ? integer
                : throw new ArgumentException($"{name}: argument {index + 1} must be an integer.");
        if (type == typeof(JsonElement))
            return element;
        throw new InvalidOperationException($"Unsupported endpoint API parameter type {type.Name}.");
    }

    private static string Envelope(bool ok, object? value, string? error, string? code = null)
        => JsonSerializer.Serialize(new HostEnvelope(ok, value, error, code));

    private sealed record HostEnvelope(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("value")] object? Value,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code);

    // ── Records ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads one record. Returns null when the record does not exist. Collection names and ids both work, as in
    /// the YAML <c>read</c> step.
    /// </summary>
    /// <param name="collection">Collection name or id.</param>
    /// <param name="key">Record key.</param>
    [ScriptApi("records.get")]
    public object? RecordsGet(string collection, string key) => _bindings.ReadRecord(collection, key);

    /// <summary>
    /// Writes one record. Writes are applied after the handler returns successfully, in one transaction, under the
    /// same size limits and version checks as YAML <c>write</c> steps.
    /// </summary>
    /// <param name="collection">Collection name or id.</param>
    /// <param name="key">Record key.</param>
    /// <param name="data">Record body; must be a JSON object.</param>
    [ScriptApi("records.put")]
    public void RecordsPut(string collection, string key, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("records.put needs a JSON object as the record data.");
        _bindings.WriteRecord(collection, key, (Dictionary<string, object?>)ScriptJson.ToPlain(data)!);
    }

    /// <summary>Deletes one record. Applied with the other writes after a successful handler return.</summary>
    /// <param name="collection">Collection name or id.</param>
    /// <param name="key">Record key.</param>
    [ScriptApi("records.delete")]
    public void RecordsDelete(string collection, string key) => _bindings.DeleteRecord(collection, key);

    /// <summary>
    /// Lists the values of every record in a collection, as they were when the endpoint started. Records written by
    /// this same call are not included. Fails with 400 SCAN_TOO_LARGE above the per-request scan cap.
    /// </summary>
    /// <param name="collection">Collection name or id.</param>
    [ScriptApi("records.list")]
    public List<object?> RecordsList(string collection) => _bindings.ScanCollection(collection);

    // ── Game values ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a published game value by dotted path, such as <c>combat.zombie_hp_per_level</c> (a group key) or
    /// <c>weapons</c> (a table of rows). Returns null when the path does not exist.
    /// </summary>
    /// <param name="path">Dotted path into the game values.</param>
    [ScriptApi("values.get")]
    public object? ValuesGet(string path)
    {
        object? current = _bindings.GameValues;
        foreach (var part in path.Split('.'))
        {
            if (current is not IReadOnlyDictionary<string, object?> map || !map.TryGetValue(part, out current))
                return null;
        }
        return current;
    }

    // ── Workflows and globals ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a workflow of this project and returns its result, the same way a YAML <c>workflow</c> step runs it:
    /// same access checks, storage semantics and limits. The workflow sees <paramref name="input"/> as its
    /// <c>input</c>. Its writes apply only if it succeeds. A failure ends the request with the workflow's own error
    /// code and status, and the script cannot catch it. Workflow webhooks are refused and sleeps are skipped.
    /// </summary>
    /// <param name="workflowId">Workflow id. Also callable by camelCase alias as <c>ns.workflows.&lt;alias&gt;(input)</c>.</param>
    /// <param name="input">Input object. The workflow reads it as <c>input</c>.</param>
    [ScriptApi("workflows.run")]
    public object? WorkflowsRun(string workflowId, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("workflows.run needs an object as the input.");
        return _bindings.RunWorkflow(workflowId, (Dictionary<string, object?>)ScriptJson.ToPlain(input)!);
    }

    /// <summary>Maps each callable workflow alias to its workflow id. Used by the script prelude only.</summary>
    [ScriptApi("workflows.catalog", Internal = true)]
    public Dictionary<string, string> WorkflowsCatalog() => ScriptWorkflowAliases.Build(_bindings.WorkflowIds());

    /// <summary>
    /// Every name a script can use for a global module (the endpoint id, or its camelCase alias) mapped to the endpoint id.
    /// The prelude defines the <c>globals</c> namespace from this. Used by the prelude only.
    /// </summary>
    [ScriptApi("globals.catalog", Internal = true)]
    public Dictionary<string, string> GlobalsCatalog()
    {
        var ids = _bindings.EndpointIds();
        var names = ScriptWorkflowAliases.Build(ids);
        foreach (var id in ids.Order(StringComparer.Ordinal))
            names[id] = id;
        return names;
    }

    /// <summary>
    /// JavaScript for the global module <paramref name="endpointId"/>, or null when the id is not a stored global
    /// endpoint of the project. Used by the module loader only; it is not a script API member.
    /// </summary>
    public string? GlobalModuleSource(string endpointId) => _bindings.GlobalSource(endpointId);

    // ── Outbound HTTP ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends an HTTP request to a host on this project's outbound allow-list (or to any public host when the owner
    /// allows all public hosts). Returns <c>{status, ok, headers, body, json}</c>. A valid call whose result is not
    /// loaded yet returns null, and the endpoint runs again with the result. A refused, failed or timed-out call throws
    /// an error with a <c>code</c> that <c>try/catch</c> can read. Uncaught, it ends the request with status 502.
    /// Private, loopback, link-local, reserved and cloud metadata addresses stay blocked, including through
    /// redirects. Limits: 5 requests per execution, 64 KiB request body, 256 KiB response, timeout 5 s (100 ms to 10 s).
    /// </summary>
    /// <param name="url">Absolute http or https URL.</param>
    /// <param name="options">Optional <c>{method, headers, body, timeoutMs}</c>.</param>
    [ScriptApi("fetch")]
    public object? Fetch(string url, JsonElement options = default) => _bindings.Fetch.Call(url, options);

    // ── Context and diagnostics ──────────────────────────────────────────────────────────────

    /// <summary>Server time in unix milliseconds, fixed at the start of the request.</summary>
    [ScriptApi("now")]
    public long Now() => _bindings.Now.ToUnixTimeMilliseconds();

    /// <summary>
    /// Writes a line to the endpoint log. Shown in Endpoint tests and in the owner's Errors detail when the endpoint
    /// fails. Live successful calls do not persist log lines. Capped per request.
    /// </summary>
    /// <param name="value">Any JSON value; strings are written as-is, other values as JSON text.</param>
    [ScriptApi("log")]
    public void Log(JsonElement value)
    {
        if (_logTruncated) return;
        var line = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        var bytes = System.Text.Encoding.UTF8.GetByteCount(line);
        if (_logBytes + bytes > _maxLogBytes)
        {
            _logs.Add("[log truncated]");
            _logTruncated = true;
            return;
        }
        _logBytes += bytes;
        _logs.Add(line);
    }

    /// <summary>
    /// Ends the request with a typed error that game clients receive as <c>{ok:false, error:{code, message}}</c>.
    /// The script cannot catch or override it: the endpoint answers with this error whatever the handler returns.
    /// </summary>
    /// <param name="code">Upper-case error code, for example <c>NOT_ENOUGH_COINS</c>.</param>
    /// <param name="message">Message shown to the game client.</param>
    /// <param name="status">HTTP status, 400 to 499. Defaults to 400.</param>
    [ScriptApi("fail")]
    public void Fail(string code, string message, int status = 400)
    {
        if (status is < 400 or > 499)
            throw new ArgumentException("ns.fail status must be between 400 and 499.");
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("ns.fail needs an error code.");
        Failure ??= new EndpointScriptFailureException(status, code, message);
        throw Failure;
    }
}
