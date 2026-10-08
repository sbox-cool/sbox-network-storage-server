using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

public static class AdminCommands
{
    public static async Task<int> RunAsync(CliContext context)
    {
        var command = context.RequirePositional(1, "create|reset-password");
        if (command is not ("create" or "reset-password")) throw new CliException($"Unknown admin command '{command}'.", CliApp.Usage);
        var config = context.LoadValidConfig();
        await using var services = CliServices.Build(config);
        await services.GetRequiredService<INetworkStorageStoreAdmin>().MigrateAsync(CancellationToken.None);
        var accounts = new OwnerAccountService(services.GetRequiredService<INetworkStorageStore>(), config);
        var current = await accounts.GetAsync(CancellationToken.None);
        if (command == "create" && current is not null) throw new CliException("An owner already exists. Use admin reset-password.");
        if (command == "reset-password" && current is null) throw new CliException("No owner exists. Use admin create.");
        var username = context.Args.Option("username") ?? context.Args.Positional(2) ?? Environment.GetEnvironmentVariable("NS_ADMIN_USERNAME");
        if (command == "create" && string.IsNullOrWhiteSpace(username))
        {
            if (Console.IsInputRedirected || context.Args.Flag("non-interactive")) throw new CliException("Use --username NAME or NS_ADMIN_USERNAME.", CliApp.Usage);
            Console.Write("Owner username: ");
            username = Console.ReadLine();
        }
        var password = ReadPassword(context, !Console.IsInputRedirected && !context.Args.Flag("non-interactive"));
        try
        {
            var owner = command == "create"
                ? await accounts.CreateAsync(username ?? string.Empty, password, CancellationToken.None)
                : await accounts.ResetPasswordAsync(password, CancellationToken.None);
            Console.WriteLine(command == "create" ? $"Created local owner '{owner.Username}'. Log in at /login." : $"Reset password for '{owner.Username}'. Existing owner sessions are invalidated.");
            return CliApp.Ok;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            throw new CliException(ex.Message);
        }
    }

    public static string ReadPassword(CliContext context, bool interactive)
    {
        var file = context.Args.Option("admin-password-file") ?? context.Args.Option("password-file");
        if (file is not null)
        {
            try { return File.ReadAllText(Path.GetFullPath(file)).TrimEnd('\r', '\n'); }
            catch (IOException ex) { throw new CliException($"Could not read password file: {ex.Message}"); }
        }
        if (Environment.GetEnvironmentVariable("NS_ADMIN_PASSWORD") is { } password) return password;
        if (!interactive) throw new CliException("Use --password-file FILE (setup: --admin-password-file FILE) or NS_ADMIN_PASSWORD. Passwords are not accepted as command-line arguments.", CliApp.Usage);
        var first = SetupCommand.ReadSecret("Owner password (at least 12 characters, input hidden): ");
        if (first != SetupCommand.ReadSecret("Confirm owner password: ")) throw new CliException("Passwords do not match.", CliApp.Usage);
        return first;
    }

    public static async Task ConfigureDuringSetupAsync(CliContext context, EffectiveConfig config, IServiceProvider services, bool interactive)
    {
        var accounts = new OwnerAccountService(services.GetRequiredService<INetworkStorageStore>(), config);
        if (await accounts.GetAsync(CancellationToken.None) is not null)
        {
            Console.WriteLine("Existing local owner retained (use admin reset-password to change its password).");
            return;
        }
        var username = context.Args.Option("admin-username") ?? Environment.GetEnvironmentVariable("NS_ADMIN_USERNAME");
        if (username is null && interactive)
        {
            Console.Write("Owner username (leave blank to use first-run browser setup): ");
            username = Console.ReadLine()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(username))
        {
            if (context.Args.Option("admin-password-file") is not null || Environment.GetEnvironmentVariable("NS_ADMIN_PASSWORD") is not null)
                throw new CliException("--admin-username or NS_ADMIN_USERNAME is required when supplying an owner password.", CliApp.Usage);
            Console.WriteLine("Owner not created yet. Start the server and open the local one-time /setup URL printed in its log.");
            return;
        }
        try
        {
            await accounts.CreateAsync(username, ReadPassword(context, interactive), CancellationToken.None);
            Console.WriteLine($"Created local owner '{username}'. Log in at /login after starting the server.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            throw new CliException(ex.Message);
        }
    }
}
