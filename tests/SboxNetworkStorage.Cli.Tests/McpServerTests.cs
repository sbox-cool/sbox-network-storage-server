using System.Text.Json.Nodes;
using SboxNetworkStorage.Server.Cli;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class McpServerTests
{
    private readonly List<IReadOnlyList<string>> _calls = [];

    private McpServer Create(int exitCode = 0, string output = "ok")
        => new((args, _) =>
        {
            _calls.Add(args);
            return Task.FromResult(new McpServer.CommandResult(exitCode, output, string.Empty));
        }, ["--config-dir", "/etc/sbox-ns"]);

    private static async Task<JsonObject> SendAsync(McpServer server, string json)
        => (await server.HandleAsync(json, CancellationToken.None))!;

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
    public async Task Tool_list_excludes_destructive_operations()
    {
        var response = await SendAsync(Create(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var names = response["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("quickstart", names);
        Assert.Contains("server_status", names);
        Assert.Contains("tunnel_status", names);
        Assert.Contains("tunnel_enable", names);
        Assert.DoesNotContain("tunnel_disable", names);
        Assert.DoesNotContain(names, n => n.Contains("delete") || n.Contains("restore") || n.Contains("import") || n.Contains("revoke"));
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
