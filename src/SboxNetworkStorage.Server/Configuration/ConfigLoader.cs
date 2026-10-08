using System.Globalization;
using Tomlyn;
using Tomlyn.Syntax;

namespace SboxNetworkStorage.Server.Configuration;

public enum SettingSource
{
    Default,
    File,
    ConfD,
    Environment,
    Flag
}

public sealed record ConfigValue(SettingDefinition Definition, object Value, SettingSource Source, string? Origin);

public sealed record ConfigIssue(string File, int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"{File}:{Line}: {Message}" : $"{File}: {Message}";
}

/// <summary>Fully resolved configuration with the origin of every value.</summary>
public sealed class EffectiveConfig
{
    public required string ConfigDirectory { get; init; }
    public required string DataDirectory { get; init; }
    public required IReadOnlyDictionary<string, ConfigValue> Values { get; init; }
    public required IReadOnlyList<string> LoadedFiles { get; init; }
    public required IReadOnlyList<ConfigIssue> Issues { get; init; }

    public bool IsValid => Issues.Count == 0;

    public string GetString(string key) => (string)Values[key].Value;
    public long GetInteger(string key) => (long)Values[key].Value;
    public bool GetBoolean(string key) => (bool)Values[key].Value;

    /// <summary>Resolves a path setting relative to <paramref name="baseDirectory"/>.</summary>
    public string GetPath(string key, string baseDirectory)
    {
        var value = GetString(key);
        return string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value, baseDirectory);
    }
}

/// <summary>
/// Loads the config folder: defaults, then server/database/updates.toml, then
/// conf.d/*.toml in lexical order, then NS_ environment variables, then flags.
/// Unknown keys, wrong types and invalid choices are reported with file and line.
/// </summary>
public static class ConfigLoader
{
    public const string ConfDirectory = "conf.d";

    public static EffectiveConfig Load(
        string? configDirectoryFlag,
        string? dataDirectoryFlag,
        IReadOnlyDictionary<string, string>? flagOverrides = null,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var configDirectory = ConfigPaths.ResolveConfigDirectory(configDirectoryFlag);
        var values = SettingDefinitions.All.ToDictionary(
            d => d.Key,
            d => new ConfigValue(d, d.DefaultValue, SettingSource.Default, null),
            StringComparer.Ordinal);
        var issues = new List<ConfigIssue>();
        var loadedFiles = new List<string>();

        foreach (var file in SettingDefinitions.Files)
        {
            var path = Path.Combine(configDirectory, file);
            if (File.Exists(path))
            {
                ApplyFile(path, file, SettingSource.File, values, issues, restrictToFile: file);
                loadedFiles.Add(path);
            }
        }

        var confD = Path.Combine(configDirectory, ConfDirectory);
        if (Directory.Exists(confD))
        {
            foreach (var path in Directory.GetFiles(confD, "*.toml").OrderBy(p => p, StringComparer.Ordinal))
            {
                ApplyFile(path, Path.Combine(ConfDirectory, Path.GetFileName(path)), SettingSource.ConfD, values, issues, restrictToFile: null);
                loadedFiles.Add(path);
            }
        }

        foreach (var definition in SettingDefinitions.All)
        {
            var raw = environment(definition.EnvironmentVariable);
            if (raw is null)
            {
                continue;
            }

            if (TryConvert(definition, raw, out var value, out var error))
            {
                values[definition.Key] = new ConfigValue(definition, value, SettingSource.Environment, definition.EnvironmentVariable);
            }
            else
            {
                issues.Add(new ConfigIssue(definition.EnvironmentVariable, 0, error));
            }
        }

        foreach (var (key, raw) in flagOverrides ?? new Dictionary<string, string>())
        {
            var definition = SettingDefinitions.Find(key);
            if (definition is null)
            {
                issues.Add(new ConfigIssue("command line", 0, $"unknown setting '{key}'"));
                continue;
            }

            if (TryConvert(definition, raw, out var value, out var error))
            {
                values[key] = new ConfigValue(definition, value, SettingSource.Flag, "command line");
            }
            else
            {
                issues.Add(new ConfigIssue("command line", 0, error));
            }
        }

        ValidateCombinations(values, issues);

        var dataDirectory = ConfigPaths.ResolveDataDirectory(dataDirectoryFlag, (string)values["server.data_dir"].Value, configDirectory);
        return new EffectiveConfig
        {
            ConfigDirectory = configDirectory,
            DataDirectory = dataDirectory,
            Values = values,
            LoadedFiles = loadedFiles,
            Issues = issues
        };
    }

    private static void ApplyFile(
        string path,
        string displayName,
        SettingSource source,
        Dictionary<string, ConfigValue> values,
        List<ConfigIssue> issues,
        string? restrictToFile)
    {
        var text = File.ReadAllText(path);
        var document = Toml.Parse(text, path);
        if (document.HasErrors)
        {
            foreach (var diagnostic in document.Diagnostics)
            {
                issues.Add(new ConfigIssue(displayName, diagnostic.Span.Start.Line + 1, diagnostic.Message));
            }

            return;
        }

        foreach (var keyValue in document.KeyValues)
        {
            ApplyKeyValue(prefix: null, keyValue, displayName, source, values, issues, restrictToFile);
        }

        foreach (var table in document.Tables)
        {
            if (table is TableArraySyntax)
            {
                issues.Add(new ConfigIssue(displayName, table.Span.Start.Line + 1, "arrays of tables are not supported"));
                continue;
            }

            var tableName = KeyText(table.Name);
            foreach (var item in table.Items)
            {
                ApplyKeyValue(tableName, item, displayName, source, values, issues, restrictToFile);
            }
        }
    }

    private static void ApplyKeyValue(
        string? prefix,
        KeyValueSyntax keyValue,
        string displayName,
        SettingSource source,
        Dictionary<string, ConfigValue> values,
        List<ConfigIssue> issues,
        string? restrictToFile)
    {
        var line = keyValue.Span.Start.Line + 1;
        var leaf = KeyText(keyValue.Key);
        var fullKey = string.IsNullOrEmpty(prefix) ? leaf : $"{prefix}.{leaf}";
        var definition = SettingDefinitions.Find(fullKey);
        if (definition is null)
        {
            var suggestion = ClosestKey(fullKey);
            issues.Add(new ConfigIssue(displayName, line,
                suggestion is null ? $"unknown setting '{fullKey}'" : $"unknown setting '{fullKey}' (did you mean '{suggestion}'?)"));
            return;
        }

        if (restrictToFile is not null && !string.Equals(definition.File, restrictToFile, StringComparison.Ordinal))
        {
            issues.Add(new ConfigIssue(displayName, line, $"'{fullKey}' belongs in {definition.File}"));
            return;
        }

        object? value = keyValue.Value switch
        {
            StringValueSyntax s when definition.Type == SettingType.String => s.Value,
            IntegerValueSyntax i when definition.Type == SettingType.Integer => i.Value,
            BooleanValueSyntax b when definition.Type == SettingType.Boolean => b.Value,
            _ => null
        };

        if (value is null)
        {
            issues.Add(new ConfigIssue(displayName, line, $"'{fullKey}' must be {Describe(definition.Type)}"));
            return;
        }

        if (!TryNormalizeChoice(definition, value, out value, out var choiceError))
        {
            issues.Add(new ConfigIssue(displayName, line, choiceError));
            return;
        }

        values[fullKey] = new ConfigValue(definition, value, source, $"{displayName}:{line}");
    }

    public static bool TryConvert(SettingDefinition definition, string raw, out object value, out string error)
    {
        error = string.Empty;
        switch (definition.Type)
        {
            case SettingType.Integer when long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number):
                value = number;
                break;
            case SettingType.Boolean when bool.TryParse(raw, out var flag):
                value = flag;
                break;
            case SettingType.String:
                value = raw;
                break;
            default:
                value = definition.DefaultValue;
                error = $"'{definition.Key}' must be {Describe(definition.Type)}, got '{raw}'";
                return false;
        }

        return TryNormalizeChoice(definition, value, out value, out error);
    }

    private static bool TryNormalizeChoice(SettingDefinition definition, object value, out object normalized, out string error)
    {
        normalized = value;
        error = string.Empty;
        if (definition.AllowedValues is null || value is not string text)
        {
            return true;
        }

        var match = definition.AllowedValues.FirstOrDefault(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            error = $"'{definition.Key}' must be one of {string.Join(", ", definition.AllowedValues.Select(v => $"\"{v}\""))}, got \"{text}\"";
            return false;
        }

        normalized = match;
        return true;
    }

    private static void ValidateCombinations(Dictionary<string, ConfigValue> values, List<ConfigIssue> issues)
    {
        string S(string key) => (string)values[key].Value;
        ConfigIssue Issue(string key, string message)
        {
            var origin = values[key].Origin ?? values[key].Definition.File;
            var separator = origin.LastIndexOf(':');
            return separator > 0 && int.TryParse(origin[(separator + 1)..], out var line)
                ? new ConfigIssue(origin[..separator], line, message)
                : new ConfigIssue(origin, 0, message);
        }

        try { _ = Tunnels.TunnelRegistryClient.ValidateRegistry(S("tunnel.registry")); }
        catch (InvalidOperationException ex) { issues.Add(Issue("tunnel.registry", ex.Message)); }
        if ((long)values["tunnel.local_port"].Value is < 1 or > 65535)
            issues.Add(Issue("tunnel.local_port", "tunnel.local_port must be between 1 and 65535."));
        if ((bool)values["tunnel.enabled"].Value)
        {
            var name = S("tunnel.name");
            if (name.Length != 12 || name.Any(c => c is not (>= 'a' and <= 'z') and not (>= '2' and <= '7')))
                issues.Add(Issue("tunnel.name", "tunnel.name must be 12 lowercase base32 characters."));
            try
            {
                var url = Tunnels.TunnelRegistryClient.PublicUrl(S("tunnel.hostname"), name);
                if (S("server.public_url") != url)
                    issues.Add(Issue("server.public_url", "Enabled tunnels require their exact HTTPS public URL."));
            }
            catch (InvalidDataException ex) { issues.Add(Issue("tunnel.hostname", ex.Message)); }
            if (!ListenAddress.TryParse(S("server.listen"), out var origin)
                || origin.Address is null || !System.Net.IPAddress.IsLoopback(origin.Address)
                || origin.Port != (long)values["tunnel.local_port"].Value || S("tls.mode") != "off")
                issues.Add(Issue("tunnel.enabled", "Enabled tunnels require a loopback HTTP origin and tls.mode = off."));
            if (!ListenAddress.TryParse(S("tunnel.previous_listen"), out _))
                issues.Add(Issue("tunnel.previous_listen", "Enabled tunnels require the saved listener to restore on disable."));
        }

        try { _ = SignedDns.DnsRegistryClient.ValidateRegistry(S("dns.registry")); }
        catch (InvalidOperationException ex) { issues.Add(Issue("dns.registry", ex.Message)); }
        if ((bool)values["dns.enabled"].Value)
        {
            var hostname = S("dns.hostname");
            if ((bool)values["tunnel.enabled"].Value)
                issues.Add(Issue("dns.enabled", "dns.enabled and tunnel.enabled cannot both be true; disable one of them."));
            if (!SignedDns.DnsRegistryClient.IsHostname(hostname))
                issues.Add(Issue("dns.hostname", "dns.hostname must look like <name>.n1.sboxns.com."));
            else if (S("server.public_url") != $"https://{hostname}" || S("tls.mode") != "acme" || S("tls.acme_domain") != hostname)
                issues.Add(Issue("dns.enabled", "Enabled DNS names require server.public_url = https://<dns.hostname>, tls.mode = acme and tls.acme_domain = <dns.hostname>."));
            try
            {
                if (SignedDns.DnsRegistryClient.NormalizeAddress(S("dns.ipv4"), System.Net.Sockets.AddressFamily.InterNetwork).Length == 0
                    && SignedDns.DnsRegistryClient.NormalizeAddress(S("dns.ipv6"), System.Net.Sockets.AddressFamily.InterNetworkV6).Length == 0)
                    issues.Add(Issue("dns.enabled", "Enabled DNS names require dns.ipv4 or dns.ipv6."));
            }
            catch (ArgumentException ex) { issues.Add(Issue("dns.enabled", ex.Message)); }
        }

        switch (S("tls.mode"))
        {
            case "certificate" when string.IsNullOrWhiteSpace(S("tls.certificate_path")) || string.IsNullOrWhiteSpace(S("tls.key_path")):
                issues.Add(Issue("tls.mode", "tls.mode = \"certificate\" requires tls.certificate_path and tls.key_path"));
                break;
            case "acme" when string.IsNullOrWhiteSpace(S("tls.acme_domain")) || string.IsNullOrWhiteSpace(S("tls.acme_email")):
                issues.Add(Issue("tls.mode", "tls.mode = \"acme\" requires tls.acme_domain and tls.acme_email"));
                break;
            case "acme" when !(bool)values["tls.acme_accept_terms"].Value:
                issues.Add(Issue("tls.mode", "tls.mode = \"acme\" requires tls.acme_accept_terms = true"));
                break;
        }

        foreach (var key in new[] { "server.listen", "tls.https_listen" })
        {
            if (!ListenAddress.TryParse(S(key), out _))
            {
                issues.Add(Issue(key, $"'{key}' must look like \"0.0.0.0:8080\", \"[::]:8080\" or \"localhost:8080\""));
            }
        }

        foreach (var key in new[] { "database.postgres.port", "database.postgres.max_pool_size", "database.postgres.connect_timeout_seconds", "database.startup_timeout_seconds", "updates.interval_hours", "alerts.smtp.port" })
        {
            if ((long)values[key].Value <= 0)
            {
                issues.Add(Issue(key, $"'{key}' must be greater than 0"));
            }
        }

        var discordUrl = S("alerts.discord.webhook_url");
        var discordUrlFile = S("alerts.discord.webhook_url_file");
        if (!string.IsNullOrWhiteSpace(discordUrl) && !string.IsNullOrWhiteSpace(discordUrlFile))
        {
            issues.Add(Issue("alerts.discord.webhook_url", "set either 'alerts.discord.webhook_url' or 'alerts.discord.webhook_url_file', not both"));
        }

        if (!string.IsNullOrWhiteSpace(discordUrl) && !discordUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Issue("alerts.discord.webhook_url", "'alerts.discord.webhook_url' must be an https:// Discord webhook URL"));
        }

        if ((bool)values["alerts.discord.enabled"].Value
            && string.IsNullOrWhiteSpace(discordUrl) && string.IsNullOrWhiteSpace(discordUrlFile))
        {
            issues.Add(Issue("alerts.discord.enabled", "'alerts.discord.enabled = true' requires 'alerts.discord.webhook_url' or 'alerts.discord.webhook_url_file'"));
        }

        var smtpPassword = S("alerts.smtp.password");
        var smtpPasswordFile = S("alerts.smtp.password_file");
        if (!string.IsNullOrWhiteSpace(smtpPassword) && !string.IsNullOrWhiteSpace(smtpPasswordFile))
        {
            issues.Add(Issue("alerts.smtp.password", "set either 'alerts.smtp.password' or 'alerts.smtp.password_file', not both"));
        }

        if ((bool)values["alerts.smtp.enabled"].Value)
        {
            foreach (var key in new[] { "alerts.smtp.host", "alerts.smtp.from", "alerts.smtp.to" })
            {
                if (string.IsNullOrWhiteSpace(S(key)))
                {
                    issues.Add(Issue(key, $"'alerts.smtp.enabled = true' requires '{key}'"));
                }
            }
        }
    }

    private static string KeyText(KeySyntax? key)
        => key is null
            ? string.Empty
            : string.Join('.', key.ToString().Split('.').Select(part => part.Trim().Trim('"', '\'')));

    private static string Describe(SettingType type) => type switch
    {
        SettingType.Integer => "a whole number",
        SettingType.Boolean => "true or false",
        _ => "a quoted string"
    };

    private static string? ClosestKey(string key)
    {
        var best = SettingDefinitions.All
            .Select(d => (d.Key, Distance: Levenshtein(key, d.Key)))
            .OrderBy(x => x.Distance)
            .First();
        return best.Distance <= Math.Max(2, key.Length / 4) ? best.Key : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
