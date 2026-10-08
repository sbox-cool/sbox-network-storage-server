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

    public static readonly IReadOnlyList<string> Files = [ServerFile, DatabaseFile, UpdatesFile];

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        new(ServerFile, "server.listen", SettingType.String, "0.0.0.0:8080",
            "Address and port the HTTP listener binds to."),
        new(ServerFile, "server.public_url", SettingType.String, "",
            "External URL players reach this server at (shown by setup and doctor). Example: https://ns.example.com"),
        new(ServerFile, "server.data_dir", SettingType.String, "",
            "Directory for the SQLite database, ACME certificates and backups. Empty uses the platform default."),
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
        new(ServerFile, "logging.level", SettingType.String, "Information",
            "Minimum log level.", ["Trace", "Debug", "Information", "Warning", "Error"]),
        new(ServerFile, "auth.session_secret_file", SettingType.String, "secrets/auth_session_secret",
            "File holding the secret that signs player auth sessions (relative to the config folder). Generated on first start."),
        new(ServerFile, "auth.storage_encryption_key_file", SettingType.String, "secrets/storage_encryption_key",
            "File holding the 64-hex-character key that derives secret API key identifiers. Generated on first start. Losing it invalidates existing secret keys."),
        new(ServerFile, "auth.security_signing_key_file", SettingType.String, "secrets/security_signing_key.pem",
            "RSA private key (PEM) that signs the published security config read by game clients. Generated on first start."),

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
            "Check for new releases once per interval and show a notice. Updates are never installed automatically."),
        new(UpdatesFile, "updates.feed_url", SettingType.String, "https://sboxcool.com/api/network-storage/releases/latest",
            "Release feed queried first."),
        new(UpdatesFile, "updates.github_repo", SettingType.String, "sbox-cool/sbox-network-storage-server",
            "GitHub repository used as the fallback release source and by `sbox-ns update`."),
        new(UpdatesFile, "updates.interval_hours", SettingType.Integer, 24L, "Hours between update checks."),
        new(UpdatesFile, "updates.include_prereleases", SettingType.Boolean, false,
            "Also announce pre-release versions."),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static SettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    public static IEnumerable<string> KnownTables => All.Select(d => d.Table).Distinct(StringComparer.Ordinal);
}
