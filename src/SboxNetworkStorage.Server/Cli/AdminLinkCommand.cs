using System.Globalization;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

/// <summary><c>sbox-ns admin login-link</c>: mint a single-use owner login link on the server.</summary>
public static class AdminLinkCommand
{
    public static async Task<int> RunAsync(CliContext context)
    {
        var minutes = ParseMinutes(context.Args.Option("minutes"));
        var config = context.LoadValidConfig();
        await using var services = CliServices.Build(config);
        await services.GetRequiredService<INetworkStorageStoreAdmin>().MigrateAsync(CancellationToken.None);
        var store = services.GetRequiredService<INetworkStorageStore>();
        var owner = await new OwnerAccountService(store, config).GetAsync(CancellationToken.None);
        var (token, expiresAt) = await new OwnerLoginLinkService(store).CreateAsync(minutes, CancellationToken.None);
        var baseUrl = ServerBaseUrl.FromConfig(config, ServerBaseUrl.DetectHostAddress);
        Console.WriteLine($"{baseUrl}/login/link?token={token}");
        Console.WriteLine(owner is null
            ? "Opens owner creation (no owner exists yet)."
            : $"Signs in as owner '{owner.Username}'.");
        Console.WriteLine($"Single use; expires {expiresAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC ({minutes} min). Opening the page does not use it up; confirming does.");
        if (baseUrl.StartsWith("http://", StringComparison.Ordinal) && !ServerBaseUrl.IsLoopback(baseUrl))
            Console.Error.WriteLine("warning: this link uses plain HTTP, so it and your session can be intercepted on the network. Prefer HTTPS (server.public_url / tls.mode) or an SSH tunnel; see docs/admin-panel.md.");
        return CliApp.Ok;
    }

    public static int ParseMinutes(string? value)
    {
        if (value is null) return OwnerLoginLinkService.DefaultMinutes;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes is < 1 or > OwnerLoginLinkService.MaxMinutes)
            throw new CliException($"--minutes must be a whole number from 1 to {OwnerLoginLinkService.MaxMinutes}.", CliApp.Usage);
        return minutes;
    }
}
