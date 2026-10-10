using System.Text.Json.Nodes;
using SboxNetworkStorage.Server.Cli;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class McpServerTests
{
    private readonly List<IReadOnlyList<string>> _calls = [];
    private readonly List<string?> _stdin = [];

    private McpServer Create(int exitCode = 0, string output = "ok")
        => new((args, stdin, _) =>
        {
            _calls.Add(args);
            _stdin.Add(stdin);
            return Task.FromResult(new McpServer.CommandResult(exitCode, output, string.Empty));
        }, ["--config-dir", "/etc/sbox-ns"]);

    private static async Task<JsonObject> SendAsync(McpServer server, string json)
        => (await server.HandleAsync(json, CancellationToken.None))!;

    private static string Call(string tool, string arguments)
        => "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}";

    [Theory]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("1999-01-01", McpServer.ProtocolVersion)]
    public async Task Initialize_negotiates_supported_protocol_version(string requested, string expected)
    {
        var response = await SendAsync(Create(),
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + requested + "\"}}");
        Assert.Equal(expected, response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task Notifications_get_no_response()
    {
        Assert.Null(await Create().HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None));
    }

    [Fact]
    public async Task Restore_import_and_address_revocation_are_never_tools()
    {
        var response = await SendAsync(Create(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var names = response["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("tunnel_disable", names);
        Assert.DoesNotContain("dns_enable", names);
        Assert.DoesNotContain("dns_disable", names);
        Assert.DoesNotContain(names, n => n.Contains("restore") || n.Contains("import"));
    }

    [Fact]
    public async Task Every_tool_that_writes_or_deletes_names_its_opt_in_setting()
    {
        var response = await SendAsync(Create(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        foreach (var (tool, setting) in DevCommands.Tools.Where(t => t.Value is not null))
        {
            var listed = response["result"]!["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == tool)!;
            Assert.Contains(setting!, listed["description"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Dev_tool_payloads_go_through_stdin_never_the_command_line()
    {
        const string source = "- id: starts-with-a-dash\n--- yaml that looks like an option";
        var response = await SendAsync(Create(output: """{"ok":true,"diagnostics":[]}"""),
            Call("definition_check", "{\"projectId\":\"proj_1\",\"kind\":\"endpoint\",\"source\":" + JsonValue.Create(source).ToJsonString() + "}"));

        Assert.Equal(["dev", "definition_check", "--config-dir", "/etc/sbox-ns"], _calls.Single());
        Assert.Equal(source, JsonNode.Parse(_stdin.Single()!)!["source"]!.GetValue<string>());
        Assert.True(response["result"]!["structuredContent"]!["ok"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("endpoint_test", """{"projectId":"p","slug":"s","input":"not an object"}""")]
    [InlineData("endpoint_test", """{"projectId":"p","slug":"s","asServer":"yes"}""")]
    [InlineData("logs_requests", """{"projectId":"p","limit":"10"}""")]
    [InlineData("data_record", """{"projectId":"p","collection":"c"}""")]
    [InlineData("usage", """{"projectId":"p","extra":1}""")]
    public async Task Dev_tool_arguments_are_type_checked_before_running(string tool, string arguments)
    {
        var response = await SendAsync(Create(), Call(tool, arguments));
        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Resources_serve_the_embedded_guides_and_prompts_fill_their_arguments()
    {
        var server = Create();
        var list = await SendAsync(server, """{"jsonrpc":"2.0","id":1,"method":"resources/list"}""");
        var uri = list["result"]!["resources"]![0]!["uri"]!.GetValue<string>();
        var read = await SendAsync(server, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"resources/read\",\"params\":{\"uri\":\"" + uri + "\"}}");
        Assert.StartsWith("#", read["result"]!["contents"]![0]!["text"]!.GetValue<string>());

        var prompt = await SendAsync(server, """{"jsonrpc":"2.0","id":3,"method":"prompts/get","params":{"name":"first-setup","arguments":{"gameName":"Ore Miner"}}}""");
        Assert.Contains("Ore Miner", prompt["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>());
        var missing = await SendAsync(server, """{"jsonrpc":"2.0","id":4,"method":"prompts/get","params":{"name":"first-setup"}}""");
        Assert.Equal(-32602, missing["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public async Task Dashboard_links_are_limited_to_fifteen_minutes(int minutes)
    {
        var response = await SendAsync(Create(), Call("dashboard_link", "{\"minutes\":" + minutes + "}"));
        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_calls);
    }

    [Fact]
    public void Agent_plugin_skills_only_name_tools_that_exist()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SboxNetworkStorage.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var skills = Directory.GetFiles(Path.Combine(root.FullName, "agent-plugin", "skills"), "SKILL.md", SearchOption.AllDirectories);
        Assert.NotEmpty(skills);
        var tools = McpServer.ToolNames.ToHashSet(StringComparer.Ordinal);
        foreach (var skill in skills)
        {
            // Inline code that looks like a tool name (lower_snake_case) must be one.
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(skill), "`([a-z]+(?:_[a-z]+)+)`"))
            {
                Assert.True(tools.Contains(match.Groups[1].Value), $"{skill} names unknown tool {match.Groups[1].Value}");
            }
        }
    }

    [Fact]
    public async Task Tool_call_runs_fixed_argument_shape_with_global_options()
    {
        var response = await SendAsync(Create(output: "{\"ok\":true}"),
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"quickstart","arguments":{"name":"My Game","publicUrl":"https://ns.example.com"}}}""");
        Assert.Equal(["quickstart", "My Game", "--json", "--public-url", "https://ns.example.com", "--config-dir", "/etc/sbox-ns"], _calls.Single());
        Assert.False(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("{\"ok\":true}", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_command_is_reported_as_tool_error()
    {
        var response = await SendAsync(Create(exitCode: 1, output: "boom"),
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"server_status"}}""");
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("""{"name":"--config-dir"}""")]
    [InlineData("""{"name":42}""")]
    [InlineData("""{}""")]
    public async Task Invalid_arguments_are_rejected_without_running_anything(string arguments)
    {
        var response = await SendAsync(Create(),
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"project_create\",\"arguments\":" + arguments + "}}");
        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("alerts.smtp.password", "alerts.smtp.password_file")]
    [InlineData("database.postgres.connection_string", "sbox-ns config set")]
    public async Task Config_set_refuses_secret_settings_without_running_anything(string key, string hint)
    {
        var response = await SendAsync(Create(),
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"config_set","arguments":{"key":""" + "\"" + key + "\"" + ""","value":"hunter2"}}}""");

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Contains(hint, response["error"]!["message"]!.GetValue<string>());
        Assert.DoesNotContain("hunter2", response.ToJsonString());
        Assert.Empty(_calls);

        await SendAsync(Create(), """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"config_set","arguments":{"key":"alerts.smtp.password_file","value":"secrets/smtp"}}}""");
        Assert.Equal(["config", "set", "alerts.smtp.password_file", "secrets/smtp", "--config-dir", "/etc/sbox-ns"], _calls.Single());
    }

    [Theory]
    [InlineData("""{"method":42}""", -32600)]
    [InlineData("""{"method":true}""", -32600)]
    [InlineData("""{"method":{}}""", -32600)]
    [InlineData("""{"method":[]}""", -32600)]
    [InlineData("""{"method":null}""", -32600)]
    [InlineData("""{"method":"initialize","params":{"protocolVersion":42}}""", -32602)]
    [InlineData("""{"method":"initialize","params":{"protocolVersion":true}}""", -32602)]
    [InlineData("""{"method":"initialize","params":{"protocolVersion":{}}}""", -32602)]
    [InlineData("""{"method":"initialize","params":{"protocolVersion":[]}}""", -32602)]
    [InlineData("""{"method":"tools/call","params":{"name":42}}""", -32602)]
    [InlineData("""{"method":"tools/call","params":{"name":true}}""", -32602)]
    [InlineData("""{"method":"tools/call","params":{"name":{}}}""", -32602)]
    [InlineData("""{"method":"tools/call","params":{"name":[]}}""", -32602)]
    [InlineData("""{"method":"tools/call","params":{"name":null}}""", -32602)]
    public async Task Malformed_string_fields_return_errors_and_allow_next_request(string fields, int errorCode)
    {
        var malformed = JsonNode.Parse(fields)!.AsObject();
        malformed["jsonrpc"] = "2.0";
        malformed["id"] = 1;
        using var input = new StringReader(malformed.ToJsonString() + "\n" +
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""" + "\n");
        using var output = new StringWriter();

        await Create().ServeAsync(input, output, CancellationToken.None);

        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        Assert.Equal(2, responses.Length);
        Assert.Equal("2.0", responses[0]["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, responses[0]["id"]!.GetValue<int>());
        Assert.Equal(errorCode, responses[0]["error"]!["code"]!.GetValue<int>());
        Assert.Equal("2.0", responses[1]["jsonrpc"]!.GetValue<string>());
        Assert.Equal(2, responses[1]["id"]!.GetValue<int>());
        Assert.Empty(responses[1]["result"]!.AsObject());
        Assert.Null(responses[1]["error"]);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Unknown_tool_and_method_and_bad_json_are_errors()
    {
        var server = Create();
        Assert.Equal(-32602, (await SendAsync(server, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"rm_rf"}}"""))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, (await SendAsync(server, """{"jsonrpc":"2.0","id":7,"method":"nope"}"""))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32700, (await SendAsync(server, "{not json"))["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_calls);
    }
}
