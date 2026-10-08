using System.Net.Mail;
using System.Net.Http.Json;
using System.Text.Json;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Cli;

public static class NoticeCommands
{
    private const string InstallIdFile = "install-id";

    public static async Task<int> RunAsync(CliContext context)
    {
        var remove = context.Args.Flag("remove");
        var email = context.Args.Option("email");
        if (remove && email is not null || !remove && string.IsNullOrWhiteSpace(email))
            throw new CliException("Use register --email ADDRESS or register --remove.", CliApp.Usage);
        if (!remove && (email!.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email.Trim()))
            throw new CliException("Use a plain email address without a display name.", CliApp.Usage);

        var config = context.LoadValidConfig();
        var path = Path.Combine(config.DataDirectory, InstallIdFile);
        if (remove && !File.Exists(path))
        {
            Console.WriteLine("This install has not registered for notices; no request was sent.");
            return CliApp.Ok;
        }
        if (!Uri.TryCreate(config.GetString("notices.registry"), UriKind.Absolute, out var registry) ||
            registry.Scheme != "https" && !(registry.Scheme == "http" && registry.IsLoopback) ||
            registry.UserInfo.Length > 0 || registry.Query.Length > 0 || registry.Fragment.Length > 0)
            throw new CliException("notices.registry must be an HTTPS endpoint (HTTP is allowed only on loopback for local tests).", CliApp.Usage);

        var id = LoadOrCreateInstallId(path);
        var url = remove ? new Uri(registry.AbsoluteUri.TrimEnd('/') + "/remove") : registry;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        HttpResponseMessage response;
        try
        {
            response = remove
                ? await client.PostAsJsonAsync(url, new { installId = id })
                : await client.PostAsJsonAsync(url, new { installId = id, version = BuildInfo.Version, email = email!.Trim() });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new CliException("The notice registry could not be reached; local registration settings were not changed.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new CliException($"Notice registration failed (HTTP {(int)response.StatusCode}); local registration settings were not changed.");
            bool confirmationRequired;
            try
            {
                using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                if (!body.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                    throw new JsonException();
                confirmationRequired = !remove && (!body.RootElement.TryGetProperty("confirmationRequired", out var confirmation) ||
                    confirmation.ValueKind != JsonValueKind.False);
            }
            catch (JsonException)
            {
                throw new CliException("The notice registry returned an invalid response; local registration settings were not changed.");
            }
            ConfigFiles.SetValue(config.ConfigDirectory, SettingDefinitions.Find("notices.email")!, remove ? "" : email!.Trim());
            Console.WriteLine(remove ? "Unsubscribed from security and update notices."
                : confirmationRequired ? "Check your inbox and confirm the subscription. No notices are sent before confirmation."
                : "This install is subscribed to security and update notices.");
            return CliApp.Ok;
        }
    }

    public static async Task ConfigureDuringSetupAsync(CliContext context, bool interactive)
    {
        if (!interactive) return;
        Console.Write("Email for optional security/update notices (blank to skip; requires email confirmation): ");
        var email = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(email)) return; // No install ID and no request when skipped.
        var args = new List<string> { "register", "--email", email };
        foreach (var key in new[] { "config-dir", "data-dir" })
            if (context.Args.Option(key) is { } value) { args.Add("--" + key); args.Add(value); }
        try { await RunAsync(new CliContext(CliArguments.Parse(args))); }
        catch (CliException ex) { Console.Error.WriteLine($"Notice registration did not complete: {ex.Message} Run sbox-ns register --email ADDRESS later."); }
    }

    private static Guid LoadOrCreateInstallId(string path)
        => PrivateIdFile.LoadOrCreate(path)
            ?? throw new CliException("The local install ID is invalid. Restore data/install-id from a backup before removing an existing subscription.");
}
