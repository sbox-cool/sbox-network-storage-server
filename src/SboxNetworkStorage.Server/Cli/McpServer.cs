using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// Model Context Protocol server over stdio (<c>sbox-ns mcp</c>). Coding
/// agents connect with a command such as <c>ssh my-vps sudo sbox-ns mcp</c>, so
/// nothing is exposed on the network and the agent acts with exactly the
/// permissions of that shell login. Every tool runs the same <c>sbox-ns</c>
/// binary as a child process with a fixed argument shape: tools are an
/// allowlist, not arbitrary command execution. Development tools run
/// <c>sbox-ns dev &lt;tool&gt;</c> with their arguments as JSON on stdin, never on
/// the command line. Tools that change definitions or data, or delete anything,
/// refuse to run until the operator turns on the matching <c>mcp.allow_*</c>
/// setting; database restore and import are never available.
/// </summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-06-18";

    private static readonly string[] SupportedProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly Func<IReadOnlyList<string>, string?, CancellationToken, Task<CommandResult>> _runCommand;
    private readonly IReadOnlyList<string> _globalArgs;

    public McpServer(Func<IReadOnlyList<string>, string?, CancellationToken, Task<CommandResult>> runCommand, IReadOnlyList<string> globalArgs)
    {
        _runCommand = runCommand;
        _globalArgs = globalArgs;
    }

    public sealed record CommandResult(int ExitCode, string Output, string Error);

    /// <summary>A tool: its argument list, and optionally a stdin payload (development tools pass their arguments there).</summary>
    private sealed record Tool(string Name, string Description, JsonObject InputSchema, Func<JsonObject, IReadOnlyList<string>> BuildArgs,
        Func<JsonObject, string?>? BuildStdin = null);

    /// <summary>Tool names, for tests and the agent plugin's consistency check.</summary>
    public static IEnumerable<string> ToolNames => Tools.Select(tool => tool.Name);

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, isRequired) in properties)
        {
            props[name] = new JsonObject { ["type"] = type, ["description"] = description };
            if (isRequired)
            {
                required.Add(name);
            }
        }

        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required, ["additionalProperties"] = false };
    }

    private static readonly Tool[] Tools =
    [
        new("server_status", "Run sbox-ns doctor: config validity, database reachability, port, TLS, disk, schema version and update status.",
            Schema(), _ => ["doctor"]),
        new("version", "Print the installed sbox-ns version.", Schema(), _ => ["version"]),
        new("tunnel_status", "Show hosted HTTPS tunnel name and connector state without credentials.", Schema(), _ => ["tunnel", "status", "--json"]),
        new("tunnel_enable", "Register or reuse a hosted HTTPS address, install verified cloudflared, and bind to loopback. Restart the server afterward. Tunnel disable is CLI-only because it revokes the public address.", Schema(), _ => ["tunnel", "enable", "--json"]),
        new("dns_status", "Show the hosted sboxns.com DNS name and published addresses (read-only; dns enable/disable are CLI-only because they publish or revoke a public address).",
            Schema(), _ => ["dns", "status", "--json"]),
        new("telemetry_status", "Show whether opt-in anonymous usage statistics are enabled, the endpoint, and whether a telemetry ID exists (read-only; enable/disable are CLI-only).",
            Schema(), _ => ["telemetry", "status"]),
        new("quickstart", "Configure the server if needed, create or reuse a project by name, ensure public and secret keys, and return the C# NetworkStorage.Configure line for the game. Safe to re-run; the secret key is only returned when newly created.",
            Schema(("name", "string", "Project name, e.g. \"My Game\".", true),
                   ("publicUrl", "string", "Address players use, e.g. https://ns.example.com (only applied on first configuration).", false),
                   ("requireSboxAuth", "boolean", "Only for a new project. Default true: every player request must carry a valid s&box token, which headless tests and plain HTTP clients do not have. Set false only for a development project.", false)),
            a =>
            {
                var args = new List<string> { "quickstart", RequireString(a, "name"), "--json" };
                if (OptionalString(a, "publicUrl") is { } url)
                {
                    args.Add("--public-url");
                    args.Add(url);
                }

                if (OptionalBoolean(a, "requireSboxAuth") == false)
                {
                    args.Add("--require-sbox-auth");
                    args.Add("false");
                }

                return args;
            }),
        new("project_list", "List projects on this server (JSON).", Schema(), _ => ["project", "list", "--json"]),
        new("project_create", "Create a project. Prefer quickstart unless separate keys are managed manually.",
            Schema(("name", "string", "Project name.", true)),
            a => ["project", "create", RequireString(a, "name")]),
        new("key_list", "List API keys for a project (JSON; secret keys are masked).",
            Schema(("projectId", "string", "Project id (proj_...).", true)),
            a => ["key", "list", RequireString(a, "projectId"), "--json"]),
        new("key_create", "Create a public (game client) or secret (editor sync / dedicated server) key. Secret keys are shown once, and the value lands in this conversation and your model provider's logs.",
            Schema(("projectId", "string", "Project id (proj_...).", true),
                   ("type", "string", "public or secret.", true),
                   ("label", "string", "Optional label.", false)),
            a =>
            {
                var type = RequireString(a, "type");
                if (type is not ("public" or "secret"))
                {
                    throw new McpToolException("type must be public or secret");
                }

                var args = new List<string> { "key", "create", RequireString(a, "projectId"), "--type", type };
                if (OptionalString(a, "label") is { } label)
                {
                    args.Add("--label");
                    args.Add(label);
                }

                return args;
            }),
        new("config_show", "Show every effective setting and its source (secrets redacted).", Schema(), _ => ["config", "show"]),
        new("config_get", "Read one setting, e.g. server.public_url. Secret settings print as ********.",
            Schema(("key", "string", "Dotted setting key.", true)),
            a => ["config", "get", RequireString(a, "key")]),
        new("config_set", "Change one setting in its config file (comments preserved). Restart the service to apply. Secret settings are refused: set their *_file variant to a file path instead.",
            Schema(("key", "string", "Dotted setting key.", true), ("value", "string", "New value.", true)),
            a =>
            {
                var key = RequireString(a, "key");
                // A secret value would land in this transcript and in the child process command line.
                if (SettingDefinitions.Find(key) is { Secret: true } definition)
                {
                    var fileVariant = SettingDefinitions.Find(definition.Key + "_file");
                    throw new McpToolException(fileVariant is null
                        ? $"{key} is a secret setting; set it outside the agent session with sbox-ns config set or sbox-ns config edit."
                        : $"{key} is a secret setting; put the value in a file and set {fileVariant.Key} to its path instead.");
                }

                return ["config", "set", key, RequireString(a, "value")];
            }),
        new("config_validate", "Validate the config folder.", Schema(), _ => ["config", "validate"]),
        new("db_status", "Show the database provider and schema version.", Schema(), _ => ["db", "status"]),
        new("db_backup", "Write a consistent database backup and return its path.", Schema(), _ => ["db", "backup"]),
        new("update_check", "Check for a newer release (never installs anything).", Schema(), _ => ["update", "--check"]),
        new("service_status", "Show the installed system service status.", Schema(), _ => ["service", "status"]),
        new("service_restart", "Validate config, then restart the installed service. Needs root.", Schema(), _ => ["service", "restart"]),
        new("dashboard_link", "Create a single-use owner dashboard sign-in link for the developer to open. The link is a credential valid for the given minutes (1 to 15); it appears in this conversation.",
            Schema(("minutes", "integer", "Minutes the link stays valid, 1 to 15. Default 10.", false)),
            a =>
            {
                var minutes = OptionalInteger(a, "minutes") ?? 10;
                if (minutes is < 1 or > 15) throw new McpToolException("minutes must be from 1 to 15");
                return ["admin", "login-link", "--minutes", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            }),

        // Development loop: definitions, dry runs, logs, data. Arguments travel as JSON on stdin.
        Dev("definitions_list", "List a project's collections, endpoints, workflows, queries and game values, plus staged (next revision) endpoints and collections.",
            P("projectId", "string", "Project id (proj_...).", true), P("kind", "string", "Optional: collection, endpoint, workflow, query or game-values.")),
        Dev("definition_get", "Read one definition as YAML source (the file the Sync Tool keeps under Editor/Network Storage/<kind>/<id>.yml), plus its staged copy if one exists.",
            P("projectId", "string", "Project id.", true), P("kind", "string", "collection, endpoint, workflow, query or game-values.", true),
            P("id", "string", "Definition id or slug (not needed for game-values).")),
        Dev("definition_check", "Validate YAML source exactly as saving would, without saving. Returns diagnostics with code, path and message.",
            P("projectId", "string", "Project id.", true), P("kind", "string", "collection, endpoint, workflow, query or game-values.", true),
            P("source", "string", "The YAML source text.", true), P("id", "string", "Existing id when editing (the id cannot change).")),
        Dev("definition_save", $"Save YAML source. Off unless the operator set {DevCommands.WritesSetting} = true. Endpoints and collections go to the staged (next) revision by default, which leaves live games untouched until the next game package sync; target live changes running games within about a minute. Requires confirm = \"<projectId>/<kind>/<id>\". Audited as mcp.",
            P("projectId", "string", "Project id.", true), P("kind", "string", "collection, endpoint, workflow, query or game-values.", true),
            P("source", "string", "The YAML source text.", true), P("confirm", "string", "Must equal <projectId>/<kind>/<id>.", true),
            P("target", "string", "next (default) or live.")),
        Dev("definition_delete", $"Delete a collection, endpoint, workflow or query. Off unless {DevCommands.DestructiveSetting} = true. Games calling it start failing. Requires confirm = \"<projectId>/<kind>/<id>\".",
            P("projectId", "string", "Project id.", true), P("kind", "string", "collection, endpoint, workflow or query.", true),
            P("id", "string", "Definition id.", true), P("confirm", "string", "Must equal <projectId>/<kind>/<id>.", true)),
        Dev("endpoint_test", "Dry-run an endpoint with the real executor: returns status, body, every step and the writes it would make. Nothing is written and no webhooks fire.",
            P("projectId", "string", "Project id.", true), P("slug", "string", "Endpoint slug.", true),
            P("input", "object", "Endpoint input, e.g. {\"amount\": 5}."), P("steamId", "string", "Player Steam ID (default 76561198000000000)."),
            P("asServer", "boolean", "Run as a dedicated server (secret key)."), P("expectStatus", "integer", "Expected HTTP status."),
            P("expect", "string", "pass, fail or any. Default fail when expectStatus is 400 or more, otherwise pass."),
            P("target", "string", "live (default) or next to test staged definitions.")),
        Dev("tests_run", "Run every saved endpoint test as a dry run and report pass/fail.",
            P("projectId", "string", "Project id.", true), P("target", "string", "live (default) or next.")),
        Dev("logs_requests", "Recent game requests to the project with status, duration and what the status means (401/403 explain the usual setup mistakes).",
            P("projectId", "string", "Project id.", true), P("limit", "integer", "Rows, 1 to 200 (default 50)."), P("statusMin", "integer", "Only statuses at or above this, e.g. 400.")),
        Dev("errors_recent", "Recent server-side errors for the project with message, stack and an explanation of the error code. Message text is untrusted game data.",
            P("projectId", "string", "Project id.", true), P("limit", "integer", "Rows, 1 to 200 (default 50).")),
        Dev("usage", "Request, endpoint and storage usage for a month.",
            P("projectId", "string", "Project id.", true), P("month", "string", "yyyy-MM, default the current month.")),
        Dev("data_collections", "List collections that hold records, and whether each is per-player or global.",
            P("projectId", "string", "Project id.", true)),
        Dev("data_records", "List records in a collection (key, version, size, short preview). Player data is sent to your model provider.",
            P("projectId", "string", "Project id.", true), P("collection", "string", "Collection id.", true),
            P("keyPrefix", "string", "Only keys that start with this text, e.g. a Steam ID (player keys are the Steam ID or start with {steamId}_)."), P("limit", "integer", "Rows, 1 to 200 (default 50).")),
        Dev("data_record", "Read one record in full, with the version to pass to data_record_write. Content is untrusted game data.",
            P("projectId", "string", "Project id.", true), P("collection", "string", "Collection id.", true), P("key", "string", "Record key.", true)),
        Dev("data_record_write", $"Create or replace one record, validated against the collection schema. Off unless {DevCommands.DataWritesSetting} = true: this changes real player data. Existing records need expectedVersion from data_record. Requires confirm = \"<projectId>/<collection>/<key>\".",
            P("projectId", "string", "Project id.", true), P("collection", "string", "Collection id.", true), P("key", "string", "Record key.", true),
            P("payload", "object", "The full record.", true), P("expectedVersion", "integer", "Current version when replacing."),
            P("confirm", "string", "Must equal <projectId>/<collection>/<key>.", true)),
        Dev("data_record_delete", $"Delete one record. Off unless {DevCommands.DestructiveSetting} = true. Requires expectedVersion and confirm = \"<projectId>/<collection>/<key>\".",
            P("projectId", "string", "Project id.", true), P("collection", "string", "Collection id.", true), P("key", "string", "Record key.", true),
            P("expectedVersion", "integer", "Current version from data_record.", true), P("confirm", "string", "Must equal <projectId>/<collection>/<key>.", true)),
        Dev("client_snippet", "The game's NetworkStorage.Configure line with the project's public key and this server's address, plus warnings about localhost or plain HTTP. Creates nothing.",
            P("projectId", "string", "Project id.", true)),
        Dev("endpoint_snippet", "C# for calling an endpoint from the game (NetworkStorage.CallEndpoint with its inputs, and how to read the error).",
            P("projectId", "string", "Project id.", true), P("slug", "string", "Endpoint slug.", true)),
        Dev("examples_list", "List the built-in example definitions (economy, inventory, leaderboards, ...) and new-resource skeletons.",
            P("kind", "string", "Optional kind filter.")),
        Dev("example_get", "Get an example's YAML source with the examples it depends on, or a new-resource skeleton for a kind.",
            P("id", "string", "Example id from examples_list."), P("skeleton", "string", "Instead of id: a kind, for its empty skeleton.")),
        Dev("key_revoke", $"Revoke an API key permanently. Off unless {DevCommands.DestructiveSetting} = true. Games using the key stop working. Requires confirm = \"<projectId>/<key>\".",
            P("projectId", "string", "Project id.", true), P("key", "string", "The key as shown by key_list.", true), P("confirm", "string", "Must equal <projectId>/<key>.", true)),
        Dev("project_delete", $"Delete a project and all of its player data. Off unless {DevCommands.DestructiveSetting} = true. Take a backup first (db_backup). Requires confirm = \"<projectId>\".",
            P("projectId", "string", "Project id.", true), P("confirm", "string", "Must equal the project id.", true)),
    ];

    private sealed record Parameter(string Name, string Type, string Description, bool Required);

    private static Parameter P(string name, string type, string description, bool required = false) => new(name, type, description, required);

    /// <summary>A development tool: <c>sbox-ns dev &lt;name&gt;</c> with the type-checked arguments as JSON on stdin.</summary>
    private static Tool Dev(string name, string description, params Parameter[] parameters)
    {
        if (!DevCommands.Tools.ContainsKey(name)) throw new InvalidOperationException($"no dev command for {name}");
        var schema = Schema(parameters.Select(p => (p.Name, p.Type, p.Description, p.Required)).ToArray());
        return new Tool(name, description, schema, _ => ["dev", name], a => CheckArguments(a, parameters).ToJsonString());
    }

    private static JsonObject CheckArguments(JsonObject arguments, Parameter[] parameters)
    {
        foreach (var (key, _) in arguments)
        {
            if (parameters.All(p => p.Name != key)) throw new McpToolException($"unknown argument '{key}'");
        }

        foreach (var parameter in parameters)
        {
            var node = arguments[parameter.Name];
            if (node is null)
            {
                if (parameter.Required) throw new McpToolException($"missing required argument '{parameter.Name}'");
                continue;
            }

            var valid = parameter.Type switch
            {
                "object" => node is JsonObject,
                "string" => node is JsonValue s && s.TryGetValue<string>(out _),
                "integer" => node is JsonValue i && i.TryGetValue<long>(out _),
                "boolean" => node is JsonValue b && b.TryGetValue<bool>(out _),
                _ => false,
            };
            if (!valid) throw new McpToolException($"argument '{parameter.Name}' must be {(parameter.Type == "integer" ? "an" : "a")} {parameter.Type}");
        }

        return arguments;
    }

    public static async Task<int> RunStdioAsync(CliContext context)
    {
        var globalArgs = new List<string>();
        foreach (var option in new[] { "config-dir", "data-dir" })
        {
            if (context.Args.Option(option) is { } value)
            {
                globalArgs.Add("--" + option);
                globalArgs.Add(value);
            }
        }

        var server = new McpServer(RunSelfAsync, globalArgs);
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        await server.ServeAsync(stdin, stdout, CancellationToken.None);
        return CliApp.Ok;
    }

    /// <summary>Reads newline-delimited JSON-RPC messages until EOF and writes one response line per request.</summary>
    public async Task ServeAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = await HandleAsync(line, cancellationToken);
            if (response is not null)
            {
                await output.WriteLineAsync(response.ToJsonString());
            }
        }
    }

    public async Task<JsonObject?> HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonObject message;
        try
        {
            message = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("message must be an object");
        }
        catch (JsonException ex)
        {
            return Error(null, -32700, $"Parse error: {ex.Message}");
        }

        var id = message["id"]?.DeepClone();
        if (message["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
        {
            return id is null ? null : Error(id, -32600, "Invalid request: method must be a string");
        }

        // Notifications (no id) never get a response.
        if (id is null)
        {
            return null;
        }

        var parameters = message["params"] as JsonObject ?? new JsonObject();
        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(parameters)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, ListTools()),
                "tools/call" => Result(id, await CallToolAsync(parameters, cancellationToken)),
                "resources/list" => Result(id, McpContent.ListResources()),
                "resources/read" => Result(id, McpContent.ReadResource(ParameterString(parameters, "uri") ?? throw new McpToolException("resources/read requires a uri"))),
                "prompts/list" => Result(id, McpContent.ListPrompts()),
                "prompts/get" => Result(id, McpContent.GetPrompt(
                    ParameterString(parameters, "name") ?? throw new McpToolException("prompts/get requires a name"),
                    parameters["arguments"] as JsonObject ?? new JsonObject())),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (McpToolException ex)
        {
            return Error(id, -32602, ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject parameters)
    {
        var requested = ParameterString(parameters, "protocolVersion");
        var version = requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : ProtocolVersion;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["resources"] = new JsonObject { ["listChanged"] = false },
                ["prompts"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject { ["name"] = "sbox-ns", ["version"] = Hosting.BuildInfo.Version },
            ["instructions"] = McpContent.Instructions,
        };
    }

    private static JsonObject ListTools()
    {
        var tools = new JsonArray();
        foreach (var tool in Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var name = ParameterString(parameters, "name") ?? throw new McpToolException("tools/call requires a tool name");
        var tool = Tools.FirstOrDefault(t => t.Name == name) ?? throw new McpToolException($"Unknown tool: {name}");
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        var commandArgs = tool.BuildArgs(arguments).Concat(_globalArgs).ToList();
        var stdin = tool.BuildStdin?.Invoke(arguments);
        var result = await _runCommand(commandArgs, stdin, cancellationToken);
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : $"{result.Output}\n{result.Error}".Trim();
        var response = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = result.ExitCode != 0,
        };
        if (result.Output.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(result.Output) is JsonObject structured) response["structuredContent"] = structured;
            }
            catch (JsonException)
            {
                // Plain text that happens to start with a brace.
            }
        }

        return response;
    }

    private static string? ParameterString(JsonObject parameters, string name)
    {
        var node = parameters[name];
        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw new McpToolException($"parameter '{name}' must be a string");
        }

        return text;
    }

    private static string RequireString(JsonObject arguments, string name)
        => OptionalString(arguments, name) ?? throw new McpToolException($"missing required argument '{name}'");

    private static string? OptionalString(JsonObject arguments, string name)
    {
        var node = arguments[name];
        if (node is null)
        {
            return null;
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw new McpToolException($"argument '{name}' must be a string");
        }

        if (text.StartsWith('-'))
        {
            // Never let a value be parsed as an option of the child command.
            throw new McpToolException($"argument '{name}' must not start with '-'");
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool? OptionalBoolean(JsonObject arguments, string name)
    {
        var node = arguments[name];
        if (node is null) return null;
        if (node is not JsonValue value || !value.TryGetValue<bool>(out var flag))
        {
            throw new McpToolException($"argument '{name}' must be a boolean");
        }

        return flag;
    }

    private static long? OptionalInteger(JsonObject arguments, string name)
    {
        var node = arguments[name];
        if (node is null) return null;
        if (node is not JsonValue value || !value.TryGetValue<long>(out var number))
        {
            throw new McpToolException($"argument '{name}' must be an integer");
        }

        return number;
    }

    private static JsonObject Result(JsonNode id, JsonObject result)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task<CommandResult> RunSelfAsync(IReadOnlyList<string> args, string? stdin, CancellationToken cancellationToken)
    {
        var processPath = Environment.ProcessPath ?? throw new McpToolException("cannot determine the sbox-ns executable path");
        var start = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        // Framework-dependent runs (dotnet sbox-ns.dll) re-launch through the host with the entry assembly.
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = processPath;
            start.ArgumentList.Add(typeof(McpServer).Assembly.Location);
        }
        else
        {
            start.FileName = processPath;
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new McpToolException("failed to start sbox-ns");
        // Payloads go through stdin so they never appear in the process list; other tools get an empty stdin.
        if (stdin is not null) await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CommandResult(process.ExitCode, (await stdout).Trim(), (await stderr).Trim());
    }
}

public sealed class McpToolException(string message) : Exception(message);
