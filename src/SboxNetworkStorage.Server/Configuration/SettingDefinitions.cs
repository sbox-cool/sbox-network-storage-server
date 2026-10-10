namespace SboxNetworkStorage.Server.Configuration;

public enum SettingType
{
    String,
    Integer,
    Boolean
}

/// <summary>One configurable key: its file, dotted TOML path, type, default and help text.</summary>
public sealed record SettingDefinition(
    string File,
    string Key,
    SettingType Type,
    object DefaultValue,
    string Description,
    IReadOnlyList<string>? AllowedValues = null,
    bool Secret = false)
{
    /// <summary>The TOML table the key lives in, e.g. <c>database.postgres</c>.</summary>
    public string Table => Key[..Key.LastIndexOf('.')];

    /// <summary>The leaf key name inside its table, e.g. <c>host</c>.</summary>
    public string Name => Key[(Key.LastIndexOf('.') + 1)..];

    /// <summary>Environment variable that overrides this key, e.g. <c>NS_DATABASE__POSTGRES__HOST</c>.</summary>
    public string EnvironmentVariable => "NS_" + Key.ToUpperInvariant().Replace(".", "__", StringComparison.Ordinal);
}

/// <summary>
/// Single source of truth for every configuration key. Templates, validation,
/// <c>config show</c>, environment overrides and docs all derive from this list.
/// </summary>
public static class SettingDefinitions
{
    public const string ServerFile = "server.toml";
    public const string DatabaseFile = "database.toml";
    public const string UpdatesFile = "updates.toml";
    public const string AlertsFile = "alerts.toml";

    public static readonly IReadOnlyList<string> Files = [ServerFile, DatabaseFile, UpdatesFile, AlertsFile];

    /// <summary>
    /// Keys older configs may still contain. They are ignored (reported by <c>doctor</c>), never an error,
    /// so an upgrade does not break a working install. Release sources are compiled into the binary.
    /// </summary>
    public static readonly IReadOnlyList<string> RetiredKeys = ["updates.feed_url", "updates.github_repo"];

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        new(ServerFile, "server.listen", SettingType.String, "0.0.0.0:8080",
            "Address and port the HTTP listener binds to."),
        new(ServerFile, "server.public_url", SettingType.String, "",
            "External URL players reach this server at (shown by setup and doctor). Example: https://ns.example.com"),
        new(ServerFile, "server.data_dir", SettingType.String, "",
            "Directory for the SQLite database, ACME certificates and backups. Empty uses the platform default."),
        new(ServerFile, "server.limits.default", SettingType.Integer, 1024L,
            "Largest request body, in KiB, accepted on routes that declare no limit of their own."),
        new(ServerFile, "server.limits.data_plane", SettingType.Integer, 256L,
            "Largest request body, in KiB, accepted on game routes (/v1, /v3, /api/storage) and auth-session routes."),
        new(ServerFile, "server.limits.management", SettingType.Integer, 8192L,
            "Largest request body, in KiB, accepted on secret-key management and sync routes (/v3/manage)."),
        new(ServerFile, "server.limits.game_burst", SettingType.Integer, 600L,
            "Requests one client address may send to game routes at once before being limited (429)."),
        new(ServerFile, "server.limits.game_per_second", SettingType.Integer, 200L,
            "Requests per second one client address may sustain on game routes."),
        new(ServerFile, "server.limits.management_burst", SettingType.Integer, 120L,
            "Requests one client address may send to management routes at once before being limited (429)."),
        new(ServerFile, "server.limits.management_per_second", SettingType.Integer, 20L,
            "Requests per second one client address may sustain on management routes."),
        new(ServerFile, "server.limits.auth_session_burst", SettingType.Integer, 120L,
            "Requests one client address may send to auth-session routes at once before being limited (429)."),
        new(ServerFile, "server.limits.auth_session_per_second", SettingType.Integer, 30L,
            "Requests per second one client address may sustain on auth-session routes."),
        new(ServerFile, "analytics.retention_days", SettingType.Integer, 90L,
            "Days to keep player analytics (timeline events and issues). Older rows are purged daily. 0 keeps them forever."),
        new(ServerFile, "tls.mode", SettingType.String, "off",
            "off = plain HTTP only; certificate = PEM files below; acme = automatic Let's Encrypt certificate.",
            ["off", "certificate", "acme"]),
        new(ServerFile, "tls.https_listen", SettingType.String, "0.0.0.0:443",
            "Address and port the HTTPS listener binds to when tls.mode is not off."),
        new(ServerFile, "tls.certificate_path", SettingType.String, "",
            "PEM certificate (full chain) used when tls.mode = \"certificate\"."),
        new(ServerFile, "tls.key_path", SettingType.String, "",
            "PEM private key used when tls.mode = \"certificate\"."),
        new(ServerFile, "tls.acme_domain", SettingType.String, "",
            "Domain name to request a Let's Encrypt certificate for when tls.mode = \"acme\"."),
        new(ServerFile, "tls.acme_email", SettingType.String, "",
            "Contact email registered with Let's Encrypt."),
        new(ServerFile, "tls.acme_accept_terms", SettingType.Boolean, false,
            "Set to true to accept the Let's Encrypt subscriber agreement (required for acme)."),
        new(ServerFile, "tls.hsts", SettingType.Boolean, true,
            "Send Strict-Transport-Security on HTTPS responses when TLS is enabled. Plain-HTTP installs never send it."),
        new(ServerFile, "logging.level", SettingType.String, "Information",
            "Minimum log level.", ["Trace", "Debug", "Information", "Warning", "Error"]),
        new(ServerFile, "auth.session_secret_file", SettingType.String, "secrets/auth_session_secret",
            "File holding the secret that signs player auth sessions (relative to the state folder, <data dir>/state). Generated on first start."),
        new(ServerFile, "auth.storage_encryption_key_file", SettingType.String, "secrets/storage_encryption_key",
            "File holding the 64-hex-character key that derives secret API key identifiers. Generated on first start. Losing it invalidates existing secret keys."),
        new(ServerFile, "auth.security_signing_key_file", SettingType.String, "secrets/security_signing_key.pem",
            "RSA private key (PEM) that signs the published security config read by game clients. Generated on first start."),
        new(ServerFile, "auth.security_signing_key_id", SettingType.String, "",
            "Key id published with the signed security config. Empty derives it from the public key; set it to keep the key id of a migrated server."),
        new(ServerFile, "adminpanel.enabled", SettingType.Boolean, true, "Serve owner administration endpoints. Disable leaves game APIs available. Restart after changing."),
        new(ServerFile, "adminpanel.demo_read_only", SettingType.Boolean, false, "Dedicated public demonstration mode: ephemeral guest dashboard, fixtures only, no owner or game writes. Never enable on an operator's real database."),
        new(ServerFile, "adminpanel.allowed_ips", SettingType.String, "", "Comma-separated IP addresses or CIDRs allowed for every owner endpoint. Empty allows all. Uses trusted server RemoteIpAddress, never reads raw forwarded headers."),
        new(ServerFile, "adminpanel.allow_insecure_http", SettingType.Boolean, false, "Accept owner passwords and login links over plain HTTP from other machines. Off by default: use an SSH tunnel, tunnel enable or TLS instead. When on, passwords travel unencrypted."),
        new(ServerFile, "adminpanel.turnstile.enabled", SettingType.Boolean, false, "Require Cloudflare Turnstile for owner login, setup and login links."),
        new(ServerFile, "adminpanel.turnstile.sitekey", SettingType.String, "", "Operator-created Turnstile widget sitekey."),
        new(ServerFile, "adminpanel.turnstile.secret", SettingType.String, "", "Turnstile Siteverify secret. Prefer NS_ADMINPANEL__TURNSTILE__SECRET.", Secret: true),
        new(ServerFile, "adminpanel.turnstile.hostname", SettingType.String, "", "Exact public widget hostname, including the configured sboxns.com tunnel hostname when used. No scheme or port."),

        new(ServerFile, "tunnel.enabled", SettingType.Boolean, false, "Run the supervised cloudflared connector. Managed by tunnel enable/disable."),
        new(ServerFile, "tunnel.registry", SettingType.String, "https://sboxcool.com/api/network-storage/tunnels", "Signed tunnel registry endpoint. HTTPS required except loopback testing."),
        new(ServerFile, "tunnel.name", SettingType.String, "", "Self-certifying name, managed by tunnel enable."),
        new(ServerFile, "tunnel.hostname", SettingType.String, "", "Public sboxns.com hostname, managed by tunnel enable."),
        new(ServerFile, "tunnel.local_port", SettingType.Integer, 8080L, "Loopback HTTP origin port, managed by tunnel enable."),
        new(ServerFile, "tunnel.previous_listen", SettingType.String, "", "Listener restored by tunnel disable."),
        new(ServerFile, "tunnel.previous_public_url", SettingType.String, "", "Public URL restored by tunnel disable."),
        new(ServerFile, "tunnel.previous_tls_mode", SettingType.String, "off", "TLS mode restored by tunnel disable.", ["off", "certificate", "acme"]),
        new(ServerFile, "dns.enabled", SettingType.Boolean, false, "Publish a signed <name>.nN.sboxns.com A/AAAA name pointing at this server's own IP. Managed by dns enable/disable."),
        new(ServerFile, "dns.registry", SettingType.String, "https://sboxcool.com/api/network-storage/dns", "Signed DNS registry endpoint. HTTPS required except loopback testing."),
        new(ServerFile, "dns.hostname", SettingType.String, "", "Public sboxns.com hostname, managed by dns enable."),
        new(ServerFile, "dns.ipv4", SettingType.String, "", "Published IPv4 address, managed by dns enable and the address updater."),
        new(ServerFile, "dns.ipv6", SettingType.String, "", "Published IPv6 address, managed by dns enable and the address updater."),
        new(ServerFile, "dns.auto_address", SettingType.Boolean, true, "Check the public address every 10 minutes and update the DNS name when it changes. dns enable with --ipv4/--ipv6 turns this off."),
        new(ServerFile, "dns.previous_public_url", SettingType.String, "", "Public URL in effect before dns enable (restored by dns disable)."),
        new(ServerFile, "dns.previous_tls_mode", SettingType.String, "off", "TLS mode in effect before dns enable (restored by dns disable).", ["off", "certificate", "acme"]),
        new(ServerFile, "dns.previous_acme_domain", SettingType.String, "", "ACME domain in effect before dns enable (restored by dns disable)."),
        new(ServerFile, "notices.registry", SettingType.String, "https://sboxcool.com/api/network-storage/notices",
            "Optional security-notice registration endpoint. No request is sent unless you explicitly register."),
        new(ServerFile, "notices.email", SettingType.String, "",
            "Email last registered for optional security/update notices. Editing this value does not subscribe or unsubscribe; use register."),
        new(ServerFile, "telemetry.enabled", SettingType.Boolean, false,
            "Send anonymous usage statistics once a day. Off by default; manage with telemetry enable/disable and inspect with telemetry preview."),
        new(ServerFile, "telemetry.endpoint", SettingType.String, "https://sboxcool.com/api/network-storage/telemetry",
            "Anonymous usage statistics endpoint. HTTPS required except loopback testing. No request is sent unless telemetry.enabled is true."),
        new(ServerFile, "mcp.allow_writes", SettingType.Boolean, false,
            "Let coding agents (sbox-ns mcp) save definitions: collections, endpoints, workflows, queries and game values. Off by default. Saves go to the staged revision unless the agent asks for live."),
        new(ServerFile, "mcp.allow_data_writes", SettingType.Boolean, false,
            "Let coding agents create and change player and global records. Off by default. A wrong write changes real player data."),
        new(ServerFile, "mcp.allow_destructive", SettingType.Boolean, false,
            "Let coding agents delete definitions and records, revoke API keys and delete projects. Off by default. These cannot be undone except from a backup."),

        new(DatabaseFile, "database.provider", SettingType.String, "sqlite",
            "Database backend.", ["sqlite", "postgres"]),
        new(DatabaseFile, "database.startup_timeout_seconds", SettingType.Integer, 60L,
            "How long startup keeps retrying an unreachable database before exiting."),
        new(DatabaseFile, "database.sqlite.path", SettingType.String, "sbox-ns.db",
            "SQLite database file (relative paths are inside the data directory)."),
        new(DatabaseFile, "database.postgres.connection_string", SettingType.String, "",
            "Full Npgsql connection string. When set, the individual fields below are ignored.", Secret: true),
        new(DatabaseFile, "database.postgres.host", SettingType.String, "localhost", "PostgreSQL host."),
        new(DatabaseFile, "database.postgres.port", SettingType.Integer, 5432L, "PostgreSQL port."),
        new(DatabaseFile, "database.postgres.database", SettingType.String, "sbox_ns", "Database name."),
        new(DatabaseFile, "database.postgres.username", SettingType.String, "sbox_ns", "Database user."),
        new(DatabaseFile, "database.postgres.password", SettingType.String, "",
            "Database password. Prefer password_file so the secret is not stored in this file.", Secret: true),
        new(DatabaseFile, "database.postgres.password_file", SettingType.String, "",
            "File containing the database password (first line is used)."),
        new(DatabaseFile, "database.postgres.ssl_mode", SettingType.String, "Prefer",
            "Npgsql SSL mode.", ["Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull"]),
        new(DatabaseFile, "database.postgres.max_pool_size", SettingType.Integer, 50L, "Maximum pooled connections."),
        new(DatabaseFile, "database.postgres.schema", SettingType.String, "network_storage",
            "Schema that holds every server table, so the database can be shared."),
        new(DatabaseFile, "database.postgres.connect_timeout_seconds", SettingType.Integer, 15L,
            "Connection attempt timeout."),

        new(UpdatesFile, "updates.check", SettingType.Boolean, true,
            "Check for new releases once per interval and show a notice. Installing is controlled by auto_install."),
        new(UpdatesFile, "updates.interval_hours", SettingType.Integer, 24L, "Hours between update checks."),
        new(UpdatesFile, "updates.include_prereleases", SettingType.Boolean, false,
            "Also announce pre-release versions."),
        new(UpdatesFile, "updates.channel", SettingType.String, "stable",
            "Release channel read from the feed. stable = releases promoted after a canary soak; canary = every new release.",
            ["stable", "canary"]),
        new(UpdatesFile, "updates.auto_install", SettingType.Boolean, false,
            "Install releases unattended (`sbox-ns update --auto`, run by the sbox-ns-update timer). Off by default."),
        new(UpdatesFile, "updates.window", SettingType.String, "03:00-05:00",
            "UTC time window \"HH:MM-HH:MM\" in which unattended updates may start. May wrap midnight; \"00:00-24:00\" is always."),
        new(UpdatesFile, "updates.min_release_age_hours", SettingType.Integer, -1L,
            "Hours a release must have been on its channel before it is installed unattended. -1 = channel default (24 for stable, 0 for canary)."),
        new(AlertsFile, "alerts.discord.enabled", SettingType.Boolean, false,
            "Send captured errors and endpoint failures to a Discord channel via webhook."),
        new(AlertsFile, "alerts.discord.webhook_url", SettingType.String, "",
            "Discord webhook URL. Prefer webhook_url_file so the secret is not stored in this file.", Secret: true),
        new(AlertsFile, "alerts.discord.webhook_url_file", SettingType.String, "",
            "File containing the Discord webhook URL (first line is used). Relative paths resolve against the config folder."),
        new(AlertsFile, "alerts.discord.username", SettingType.String, "sbox-ns",
            "Username shown on Discord alert messages."),
        new(AlertsFile, "alerts.smtp.enabled", SettingType.Boolean, false,
            "Send captured errors and endpoint failures as plain-text email."),
        new(AlertsFile, "alerts.smtp.host", SettingType.String, "",
            "SMTP server hostname."),
        new(AlertsFile, "alerts.smtp.port", SettingType.Integer, 587L, "SMTP server port."),
        new(AlertsFile, "alerts.smtp.username", SettingType.String, "",
            "SMTP username. Empty means no authentication."),
        new(AlertsFile, "alerts.smtp.password", SettingType.String, "",
            "SMTP password. Prefer password_file so the secret is not stored in this file.", Secret: true),
        new(AlertsFile, "alerts.smtp.password_file", SettingType.String, "",
            "File containing the SMTP password (first line is used). Relative paths resolve against the config folder."),
        new(AlertsFile, "alerts.smtp.from", SettingType.String, "",
            "Sender address shown on alert mail, e.g. sbox-ns@example.com."),
        new(AlertsFile, "alerts.smtp.to", SettingType.String, "",
            "Recipient address (comma or semicolon separated for several)."),
        new(AlertsFile, "alerts.smtp.use_tls", SettingType.Boolean, true,
            "Upgrade the SMTP connection with STARTTLS."),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static SettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    public static IEnumerable<string> KnownTables => All.Select(d => d.Table).Distinct(StringComparer.Ordinal);
}

