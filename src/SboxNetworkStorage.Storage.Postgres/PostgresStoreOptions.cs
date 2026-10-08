using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace SboxNetworkStorage.Storage.Postgres;

/// <summary>
/// Settings for <see cref="PostgresNetworkStorageStore"/>. Either supply a full
/// <see cref="ConnectionString"/> or the individual fields. Secrets never appear
/// in <see cref="RedactedTarget"/> or <see cref="ToString"/>.
/// </summary>
public sealed partial record PostgresStoreOptions
{
    /// <summary>Full Npgsql connection string; when set, Host/Port/Database/Username/Password/SslMode/MaxPoolSize/ConnectTimeoutSeconds are ignored.</summary>
    public string? ConnectionString { get; init; }

    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Database { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string? Password { get; init; }

    /// <summary>File holding the password (read when the connection string is built; one trailing newline is ignored).</summary>
    public string? PasswordFile { get; init; }

    /// <summary>Npgsql SSL mode: Disable, Allow, Prefer, Require, VerifyCA, VerifyFull.</summary>
    public string SslMode { get; init; } = "Prefer";

    public int MaxPoolSize { get; init; } = 50;

    /// <summary>Schema holding every table; created when missing.</summary>
    public string Schema { get; init; } = "network_storage";

    public int ConnectTimeoutSeconds { get; init; } = 15;

    /// <summary>Maximum accepted JSON payload size in UTF-8 bytes (production default: 64 KiB).</summary>
    public int MaxPayloadBytes { get; init; } = 64 * 1024;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex SchemaPattern();

    /// <summary>Throws <see cref="ArgumentException"/> when the schema name is not a plain identifier.</summary>
    public void ValidateSchema()
    {
        if (string.IsNullOrEmpty(Schema) || !SchemaPattern().IsMatch(Schema))
            throw new ArgumentException($"PostgreSQL schema '{Schema}' is invalid: use 1-63 letters, digits or underscores, not starting with a digit.");
    }

    /// <summary>
    /// Builds the Npgsql connection string. Reads <see cref="PasswordFile"/> now,
    /// so file changes apply on the next build (i.e. restart).
    /// </summary>
    public string BuildConnectionString()
    {
        if (Password is not null && PasswordFile is not null)
            throw new ArgumentException("Set either the PostgreSQL password or password_file, not both.");

        NpgsqlConnectionStringBuilder builder;
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            if (string.IsNullOrEmpty(builder.Password) && PasswordFile is not null)
                builder.Password = ReadPasswordFile(PasswordFile);
            if (string.IsNullOrEmpty(builder.ApplicationName)) builder.ApplicationName = "sbox-ns";
            return builder.ConnectionString;
        }

        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("PostgreSQL host is required.");
        if (Port is <= 0 or > 65535) throw new ArgumentException($"PostgreSQL port {Port} is out of range.");
        if (string.IsNullOrWhiteSpace(Database)) throw new ArgumentException("PostgreSQL database name is required.");
        if (string.IsNullOrWhiteSpace(Username)) throw new ArgumentException("PostgreSQL username is required.");
        if (MaxPoolSize <= 0) throw new ArgumentException("PostgreSQL max pool size must be positive.");
        if (ConnectTimeoutSeconds <= 0) throw new ArgumentException("PostgreSQL connect timeout must be positive.");
        if (!Enum.TryParse<Npgsql.SslMode>(SslMode, ignoreCase: true, out var sslMode) || !Enum.IsDefined(sslMode))
            throw new ArgumentException($"PostgreSQL ssl mode '{SslMode}' is invalid; use one of: {string.Join(", ", Enum.GetNames<Npgsql.SslMode>())}.");

        builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = Username,
            Password = Password ?? (PasswordFile is null ? null : ReadPasswordFile(PasswordFile)),
            SslMode = sslMode,
            MaxPoolSize = MaxPoolSize,
            Timeout = ConnectTimeoutSeconds,
            ApplicationName = "sbox-ns",
        };
        return builder.ConnectionString;
    }

    /// <summary>Credential-free target description, e.g. <c>postgres://user@host:5432/db?schema=network_storage</c>.</summary>
    public string RedactedTarget
    {
        get
        {
            string host, database, username;
            int port;
            if (!string.IsNullOrWhiteSpace(ConnectionString))
            {
                try
                {
                    var parsed = new NpgsqlConnectionStringBuilder(ConnectionString);
                    (host, port, database, username) = (parsed.Host ?? "?", parsed.Port, parsed.Database ?? "?", parsed.Username ?? "?");
                }
                catch (ArgumentException)
                {
                    return $"postgres://(invalid connection string)?schema={Schema}";
                }
            }
            else
            {
                (host, port, database, username) = (Host, Port, Database, Username);
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"postgres://{Uri.EscapeDataString(username)}@{host}:{port}/{Uri.EscapeDataString(database)}?schema={Schema}");
        }
    }

    /// <summary>Never prints the password or connection string.</summary>
    public override string ToString() => RedactedTarget;

    private static string ReadPasswordFile(string path)
    {
        try
        {
            return File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot read PostgreSQL password file '{path}': {ex.Message}", ex);
        }
    }
}
