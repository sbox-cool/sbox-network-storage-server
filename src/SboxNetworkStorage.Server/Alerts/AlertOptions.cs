using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>
/// Resolved operator-alerting settings. Secret values come from the merged
/// config value first, then from a secret file (first line, like
/// <c>database.postgres.password_file</c>). The raw secrets are held here only;
/// they are never written to logs — <c>config show</c> redacts them via the
/// <c>Secret</c> flag on <see cref="SettingDefinitions"/>.
/// </summary>
public sealed record AlertOptions(
    bool DiscordEnabled,
    string DiscordWebhookUrl,
    string DiscordUsername,
    bool SmtpEnabled,
    string SmtpHost,
    int SmtpPort,
    string SmtpUsername,
    string SmtpPassword,
    string SmtpFrom,
    IReadOnlyList<string> SmtpTo,
    bool SmtpUseTls)
{
    public static AlertOptions Bind(EffectiveConfig config)
    {
        string S(string key) => config.GetString(key);
        return new AlertOptions(
            DiscordEnabled: config.GetBoolean("alerts.discord.enabled"),
            DiscordWebhookUrl: ResolveSecret(config, S("alerts.discord.webhook_url"), S("alerts.discord.webhook_url_file")),
            DiscordUsername: S("alerts.discord.username"),
            SmtpEnabled: config.GetBoolean("alerts.smtp.enabled"),
            SmtpHost: S("alerts.smtp.host"),
            SmtpPort: checked((int)config.GetInteger("alerts.smtp.port")),
            SmtpUsername: S("alerts.smtp.username"),
            SmtpPassword: ResolveSecret(config, S("alerts.smtp.password"), S("alerts.smtp.password_file")),
            SmtpFrom: S("alerts.smtp.from"),
            SmtpTo: S("alerts.smtp.to")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            SmtpUseTls: config.GetBoolean("alerts.smtp.use_tls"));
    }

    internal static string ResolveSecret(EffectiveConfig config, string value, string file)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            return string.Empty;
        }

        var path = Path.IsPathRooted(file) ? file : Path.GetFullPath(file, config.ConfigDirectory);
        try
        {
            // First line wins, mirroring database.postgres.password_file.
            return File.ReadAllText(path).Split('\n')[0].TrimEnd('\r');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
