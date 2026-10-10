using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// Model Context Protocol server over stdio (<c>sbox-ns mcp</c>). Coding
/// agents connect with a command such as <c>ssh my-vps sbox-ns mcp</c>, so
/// nothing is exposed on the network and the agent acts with exactly the
/// permissions of that shell login. Every tool runs the same <c>sbox-ns</c>
/// binary as a child process with a fixed argument shape: tools are an
/// allowlist, not arbitrary command execution, and destructive operations
/// (restore, import, project delete, key revoke) are deliberately absent.
/// </summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-06-18";

    private static readonly string[] SupportedProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> _runCommand;
    private readonly IReadOnlyList<string> _globalArgs;

    public McpServer(Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> runCommand, IReadOnlyList<string> globalArgs)
    {
        _runCommand = runCommand;
        _globalArgs = globalArgs;
    }

    public sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed record Tool(string Name, string Description, JsonObject InputSchema, Func<JsonObject, IReadOnlyList<string>> BuildArgs);

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
                   ("publicUrl", "string", "Address players use, e.g. https://ns.example.com (only applied on first configuration).", false)),
            a =>
            {
                var args = new List<string> { "quickstart", RequireString(a, "name"), "--json" };
                if (OptionalString(a, "publicUrl") is { } url)
                {
                    args.Add("--public-url");
                    args.Add(url);
                }

                return args;
            }),
        new("project_list", "List projects on this server.", Schema(), _ => ["project", "list"]),
        new("project_create", "Create a project. Prefer quickstart unless separate keys are managed manually.",
            Schema(("name", "string", "Project name.", true)),
            a => ["project", "create", RequireString(a, "name")]),
        new("key_list", "List API keys for a project (secret keys are masked).",
            Schema(("projectId", "string", "Project id (proj_...).", true)),
            a => ["key", "list", RequireString(a, "projectId")]),
        new("key_create", "Create a public (game client) or secret (editor sync / dedicated server) key. Secret keys are shown once.",
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
        new("service_restart", "Validate config, then restart the installed service.", Schema(), _ => ["service", "restart"]),
    ];

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
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "sbox-ns", ["version"] = Hosting.BuildInfo.Version },
            ["instructions"] = "Manage a self-hosted sbox Network Storage server. Start with server_status; use quickstart to create a project and get the game's NetworkStorage.Configure line.",
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
        var result = await _runCommand(commandArgs, cancellationToken);
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : $"{result.Output}\n{result.Error}".Trim();
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = result.ExitCode != 0,
        };
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

    private static JsonObject Result(JsonNode id, JsonObject result)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task<CommandResult> RunSelfAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
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
        process.StandardInput.Close(); // tools are non-interactive
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CommandResult(process.ExitCode, (await stdout).Trim(), (await stderr).Trim());
    }
}

public sealed class McpToolException(string message) : Exception(message);
