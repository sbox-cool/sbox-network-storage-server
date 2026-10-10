using System.Text.Json;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

/// <summary>Project and API key management (the managed dashboard's equivalents, for the single local owner).</summary>
public static class ProjectCommands
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;

    public static async Task<int> RunProjectAsync(CliContext context)
    {
        var sub = context.RequirePositional(1, "create|list|delete");
        await using var services = await OpenAsync(context);
        await using var scope = services.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var workspace = scope.ServiceProvider.GetRequiredService<IWorkspaceStore>();
        var ct = CancellationToken.None;

        switch (sub)
        {
            case "create":
            {
                var name = context.RequirePositional(2, "name");
                var keyMode = context.Args.Option("key-mode") ?? "player";
                if (keyMode is not ("player" or "public"))
                {
                    throw new CliException("--key-mode must be player or public", CliApp.Usage);
                }

                var requireSboxAuth = !string.Equals(context.Args.Option("require-sbox-auth"), "false", StringComparison.OrdinalIgnoreCase);
                var result = await projects.CreateProjectAsync(Owner, name, context.Args.Option("description") ?? string.Empty,
                    enabled: true, requireSboxAuth, keyMode, organizationId: string.Empty, ct);
                var projectId = result.ProjectId ?? throw new CliException("project creation failed");
                await scope.ServiceProvider.GetRequiredService<IAuditLogger>().LogActionAsync(new AuditLogRequest(
                    ProjectId: projectId,
                    UserId: Owner.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Action: "project.create",
                    Actor: new { id = Owner, type = "cli" },
                    Target: new { id = projectId, type = "project" },
                    Summary: new { message = $"Created project {name}" },
                    Before: null,
                    After: new { name, requireSboxAuth, keyMode }), ct);

                Console.WriteLine($"Created project {name}");
                Console.WriteLine($"Project ID: {projectId}");
                Console.WriteLine($"Next: sbox-ns key create {projectId} --type public");
                Console.WriteLine($"      sbox-ns key create {projectId} --type secret");
                return CliApp.Ok;
            }
            case "list":
            {
                var list = await workspace.GetUserProjectsAsync(Owner, ct);
                if (context.Args.Flag("json"))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        projects = list.OrderBy(p => p.CreatedAt).Select(p => new
                        {
                            projectId = p.Id, name = p.Name, enabled = p.Enabled, requireSboxAuth = p.RequireSboxAuth ?? true,
                        }),
                    }, JsonOutput));
                    return CliApp.Ok;
                }

                if (list.Count == 0)
                {
                    Console.WriteLine("No projects yet. Create one: sbox-ns project create \"My Game\"");
                    return CliApp.Ok;
                }

                Console.WriteLine($"{"PROJECT ID",-30} {"ENABLED",-8} {"S&BOX AUTH",-11} NAME");
                foreach (var project in list.OrderBy(p => p.CreatedAt))
                {
                    Console.WriteLine($"{project.Id,-30} {(project.Enabled ? "yes" : "no"),-8} {(project.RequireSboxAuth ?? true ? "required" : "off"),-11} {project.Name}");
                }

                return CliApp.Ok;
            }
            case "delete":
            {
                var projectId = context.RequirePositional(2, "projectId");
                await RequireProjectAsync(workspace, projectId, ct);
                if (!context.Args.Flag("yes", "y"))
                {
                    Console.Write($"Delete project {projectId} and all of its data? Type the project id to confirm: ");
                    if (!string.Equals(Console.ReadLine()?.Trim(), projectId, StringComparison.Ordinal))
                    {
                        throw new CliException("aborted");
                    }
                }

                await projects.DeleteProjectAsync(Owner, projectId, ct);
                Console.WriteLine($"Deleted project {projectId}");
                return CliApp.Ok;
            }
            default:
                throw new CliException($"unknown project command '{sub}'", CliApp.Usage);
        }
    }

    public static async Task<int> RunKeyAsync(CliContext context)
    {
        var sub = context.RequirePositional(1, "create|list|revoke");
        var projectId = context.RequirePositional(2, "projectId");
        await using var services = await OpenAsync(context);
        await using var scope = services.CreateAsyncScope();
        var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
        var ct = CancellationToken.None;
        await RequireProjectAsync(scope.ServiceProvider.GetRequiredService<IWorkspaceStore>(), projectId, ct);

        switch (sub)
        {
            case "create":
            {
                var type = context.Args.Option("type") ?? throw new CliException("--type public|secret is required", CliApp.Usage);
                if (type is not ("public" or "secret"))
                {
                    throw new CliException("--type must be public or secret", CliApp.Usage);
                }

                var label = context.Args.Option("label") ?? (type == "public" ? "Game client" : "Editor sync");
                var (key, raw) = await projects.CreateProjectKeyAsync(Owner, projectId, label, type, permissions: null, ct);
                Console.WriteLine($"Created {type} key \"{key.Label}\" for project {projectId}:");
                Console.WriteLine();
                Console.WriteLine($"  {raw}");
                Console.WriteLine();
                Console.WriteLine(type == "secret"
                    ? "This secret key is shown once. Store it in the editor Setup window or a dedicated server launch argument; never ship it with your game."
                    : "Use this public key in NetworkStorage.Configure(projectId, apiKey, baseUrl).");
                return CliApp.Ok;
            }
            case "list":
            {
                var keys = await projects.GetProjectKeysAsync(Owner, projectId, ct);
                if (context.Args.Flag("json"))
                {
                    // Secret keys are stored and listed masked; only public keys appear in full.
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        projectId,
                        keys = keys.Select(k => new { type = k.KeyType, enabled = k.Enabled, label = k.Label, key = k.Key }),
                    }, JsonOutput));
                    return CliApp.Ok;
                }

                if (keys.Count == 0)
                {
                    Console.WriteLine($"No keys. Create one: sbox-ns key create {projectId} --type public");
                    return CliApp.Ok;
                }

                Console.WriteLine($"{"TYPE",-7} {"ENABLED",-8} {"LABEL",-24} KEY");
                foreach (var key in keys)
                {
                    Console.WriteLine($"{key.KeyType,-7} {(key.Enabled ? "yes" : "no"),-8} {key.Label,-24} {key.Key}");
                }

                return CliApp.Ok;
            }
            case "revoke":
            {
                var key = context.RequirePositional(3, "key");
                await projects.RemoveProjectKeyAsync(Owner, projectId, key, ct);
                Console.WriteLine($"Revoked key {key}");
                return CliApp.Ok;
            }
            default:
                throw new CliException($"unknown key command '{sub}'", CliApp.Usage);
        }
    }

    private static readonly JsonSerializerOptions JsonOutput = new() { WriteIndented = true };

    private static async Task<ServiceProvider> OpenAsync(CliContext context)
    {
        var config = context.LoadValidConfig();
        var services = CliServices.Build(config);
        await services.GetRequiredService<INetworkStorageStoreAdmin>().MigrateAsync(CancellationToken.None);
        return services;
    }

    private static async Task RequireProjectAsync(IWorkspaceStore workspace, string projectId, CancellationToken ct)
    {
        var projects = await workspace.GetUserProjectsAsync(Owner, ct);
        if (!projects.Any(p => string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CliException($"project {projectId} does not exist. Run `sbox-ns project list`.");
        }
    }
}
