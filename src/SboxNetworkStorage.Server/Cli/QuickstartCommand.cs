using System.Globalization;
using System.Text.Json;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// One command from "binary installed" to "game can connect": configures the
/// server if it has no config yet (SQLite, non-interactive), then creates or
/// reuses a project by name, ensures a public and a secret key, and prints the
/// exact <c>NetworkStorage.Configure(...)</c> line for the game. Idempotent:
/// re-running with the same name reuses the project and its public key and
/// never mints a second secret key (secrets are only shown once).
/// <c>--json</c> prints a single machine-readable object for agents and MCP.
/// </summary>
public static class QuickstartCommand
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;

    public static async Task<int> RunAsync(CliContext context)
    {
        var name = context.RequirePositional(1, "project name");
        var json = context.Args.Flag("json");
        var ct = CancellationToken.None;

        var configured = context.LoadConfig().LoadedFiles.Count > 0;
        if (!configured)
        {
            var setupResult = await RunSetupAsync(context);
            if (setupResult != CliApp.Ok)
            {
                return setupResult;
            }
        }

        var config = context.LoadValidConfig();
        var runtimeArguments = new List<string> { "quickstart", name };
        foreach (var option in context.Args.Options)
        {
            runtimeArguments.Add("--" + option.Key);
            runtimeArguments.Add(option.Value);
        }
        if (json) runtimeArguments.Add("--json");
        if (await RuntimeCommand.TryRunAsync(config, runtimeArguments) is { } runtimeExit)
            return runtimeExit;
        await using var services = CliServices.Build(config);
        await services.GetRequiredService<INetworkStorageStoreAdmin>().MigrateAsync(ct);
        await using var scope = services.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var workspace = scope.ServiceProvider.GetRequiredService<IWorkspaceStore>();

        var existing = (await workspace.GetUserProjectsAsync(Owner, ct))
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.CreatedAt)
            .FirstOrDefault();

        string projectId;
        var projectCreated = false;
        if (existing is not null)
        {
            projectId = existing.Id;
        }
        else
        {
            var keyMode = context.Args.Option("key-mode") ?? "player";
            if (keyMode is not ("player" or "public"))
            {
                throw new CliException("--key-mode must be player or public", CliApp.Usage);
            }

            var requireSboxAuth = !string.Equals(context.Args.Option("require-sbox-auth"), "false", StringComparison.OrdinalIgnoreCase);
            var created = await projects.CreateProjectAsync(Owner, name, context.Args.Option("description") ?? string.Empty,
                enabled: true, requireSboxAuth, keyMode, organizationId: string.Empty, ct);
            projectId = created.ProjectId ?? throw new CliException("project creation failed");
            projectCreated = true;
            await scope.ServiceProvider.GetRequiredService<IAuditLogger>().LogActionAsync(new AuditLogRequest(
                ProjectId: projectId,
                UserId: Owner.ToString(CultureInfo.InvariantCulture),
                Action: "project.create",
                Actor: new { id = Owner, type = "cli-quickstart" },
                Target: new { id = projectId, type = "project" },
                Summary: new { message = $"Created project {name}" },
                Before: null,
                After: new { name, requireSboxAuth, keyMode }), ct);
        }

        var keys = await projects.GetProjectKeysAsync(Owner, projectId, ct);
        var publicKey = keys.FirstOrDefault(k => k.Enabled && k.KeyType == "public")?.Key;
        if (publicKey is null)
        {
            (_, publicKey) = await projects.CreateProjectKeyAsync(Owner, projectId, "Game client", "public", permissions: null, ct);
        }

        string? secretKey = null;
        if (!keys.Any(k => k.Enabled && k.KeyType == "secret"))
        {
            (_, secretKey) = await projects.CreateProjectKeyAsync(Owner, projectId, "Editor sync", "secret", permissions: null, ct);
        }

        // A wildcard bind keeps the placeholder: the CLI cannot know the address players use.
        var baseUrl = ServerBaseUrl.FromConfig(config, () => ServerBaseUrl.HostPlaceholder);
        var csharp = $"NetworkStorage.Configure( \"{projectId}\", \"{publicKey}\", \"{baseUrl}\" );";

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                projectId,
                projectName = existing?.Name ?? name,
                projectCreated,
                publicKey,
                secretKey,
                secretKeyNote = secretKey is null
                    ? "A secret key already exists and is only shown when created. Create another with: sbox-ns key create " + projectId + " --type secret"
                    : "Shown once. Use it in the s&box editor sync tool or on dedicated servers; never ship it in a game build.",
                baseUrl,
                csharp,
                configDirectory = config.ConfigDirectory,
                dataDirectory = config.DataDirectory,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return CliApp.Ok;
        }

        Console.WriteLine();
        Console.WriteLine(projectCreated ? $"Created project \"{name}\" ({projectId})." : $"Using existing project \"{existing!.Name}\" ({projectId}).");
        Console.WriteLine();
        Console.WriteLine("Add this line to your game (for example in a GameObjectSystem or your startup component):");
        Console.WriteLine();
        Console.WriteLine($"    {csharp}");
        Console.WriteLine();
        if (secretKey is not null)
        {
            Console.WriteLine("Secret key (shown once; paste it into the Network Storage setup window in the s&box editor):");
            Console.WriteLine();
            Console.WriteLine($"    {secretKey}");
            Console.WriteLine();
            Console.WriteLine("Never ship the secret key in a game build.");
        }
        else
        {
            Console.WriteLine($"A secret key already exists for this project. Create another with: sbox-ns key create {projectId} --type secret");
        }

        Console.WriteLine();
        if (baseUrl.Contains(ServerBaseUrl.HostPlaceholder, StringComparison.Ordinal))
        {
            Console.WriteLine("Replace <this-host> with your server's address, or set it once with:");
            Console.WriteLine("    sbox-ns config set server.public_url https://your.domain && sbox-ns service restart");
        }

        Console.WriteLine("Start the server if it is not running: sbox-ns start   (or: sbox-ns service install && sbox-ns service start)");
        return CliApp.Ok;
    }

    private static async Task<int> RunSetupAsync(CliContext context)
    {
        var args = new List<string> { "setup", "--non-interactive" };
        foreach (var option in new[] { "config-dir", "data-dir", "listen", "public-url", "database", "pg-connection-string", "admin-username", "admin-password-file" })
        {
            if (context.Args.Option(option) is { } value)
            {
                args.Add("--" + option);
                args.Add(value);
            }
        }

        // Setup's own progress and "next steps" would contradict quickstart's
        // summary (and break --json), so buffer it and replay it only on failure.
        var original = Console.Out;
        using var buffer = new StringWriter();
        Console.SetOut(buffer);
        int result;
        try
        {
            result = await SetupCommand.RunAsync(new CliContext(CliArguments.Parse(args)));
        }
        catch
        {
            Console.SetOut(original);
            Console.Error.Write(buffer.ToString());
            throw;
        }
        finally
        {
            Console.SetOut(original);
        }

        if (result != CliApp.Ok)
        {
            Console.Error.Write(buffer.ToString());
        }

        return result;
    }
}
