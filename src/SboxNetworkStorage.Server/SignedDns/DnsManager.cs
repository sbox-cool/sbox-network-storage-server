using System.Net;
using System.Net.Sockets;
using System.Text;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.SignedDns;

public sealed record DnsEnableOptions(string? Ipv4 = null, string? Ipv6 = null, bool AcceptLetsEncryptTerms = false, string? Email = null);

public sealed record DnsStatus(bool Enabled, string Name, string Hostname, string Registry, string Ipv4, string Ipv6,
    bool AutoAddress, int ProofPort)
{
    public static DnsStatus Read(EffectiveConfig config)
    {
        var hostname = config.GetString("dns.hostname");
        var port = ListenAddress.TryParse(config.GetString("server.listen"), out var listen) ? listen.Port : 0;
        return new(config.GetBoolean("dns.enabled"), hostname.Split('.')[0], hostname, config.GetString("dns.registry"),
            config.GetString("dns.ipv4"), config.GetString("dns.ipv6"), config.GetBoolean("dns.auto_address"), port);
    }
}

/// <summary>
/// Signed DNS lifecycle. All server/TLS overrides live in one managed conf.d overlay that sorts after the tunnel
/// overlay; disable removes them so the values that were in effect before enable apply again.
/// </summary>
public sealed class DnsManager(DnsRegistryClient registry)
{
    public const string ManagedFile = "zzzzz-dns.toml";
    private static readonly string[] ManagedKeys = ["server.public_url", "tls.mode", "tls.acme_domain", "tls.acme_email", "tls.acme_accept_terms"];
    private static string OverlayPath(EffectiveConfig config) => Path.Combine(config.ConfigDirectory, ConfigLoader.ConfDirectory, ManagedFile);

    public async Task EnableAsync(EffectiveConfig config, DnsEnableOptions options, CancellationToken ct)
    {
        using var lease = Acquire(config);
        foreach (var value in config.Values.Values)
            if (value.Definition.Key is "server.listen" or "server.public_url" or "tls.mode" && value.Source == SettingSource.Flag)
                throw new InvalidOperationException("Remove listener/public URL flags before changing DNS state.");
        config = Reload(config);
        if (config.GetBoolean("tunnel.enabled"))
            throw new InvalidOperationException("The HTTPS tunnel is enabled. Run `sbox-ns tunnel disable` first; tunnel and DNS names cannot be used together.");
        EnsureWritable(config);
        var port = ProofPort(config);
        if (!ListenAddress.TryParse(config.GetString("tls.https_listen"), out var https) || https.Port != 443)
            throw new InvalidOperationException("tls.https_listen must use port 443 for a hosted DNS name (Let's Encrypt and players connect on 443).");
        var email = string.IsNullOrWhiteSpace(options.Email) ? config.GetString("tls.acme_email") : options.Email.Trim();
        if (!(options.AcceptLetsEncryptTerms || config.GetBoolean("tls.acme_accept_terms")) || !IsEmail(email))
            throw new InvalidOperationException("A hosted DNS name uses a Let's Encrypt certificate. Pass --accept-letsencrypt-terms and --email <address> (or set tls.acme_accept_terms and tls.acme_email).");

        var enabled = config.GetBoolean("dns.enabled");
        if (enabled && !File.Exists(TunnelManager.IdentityPath(config)))
            throw new InvalidOperationException("The original identity key is missing; restore it before re-enabling.");
        using var identity = TunnelIdentity.LoadOrCreate(TunnelManager.IdentityPath(config));
        if (enabled && !config.GetString("dns.hostname").StartsWith(identity.Name + ".", StringComparison.Ordinal))
            throw new InvalidOperationException("The identity key does not match the configured DNS name.");

        var registryUrl = config.GetString("dns.registry");
        var ipv4 = DnsRegistryClient.NormalizeAddress(options.Ipv4, AddressFamily.InterNetwork);
        var ipv6 = DnsRegistryClient.NormalizeAddress(options.Ipv6, AddressFamily.InterNetworkV6);
        var explicitAddress = ipv4.Length > 0 || ipv6.Length > 0;
        if (!explicitAddress)
            (ipv4, ipv6) = WithAddress("", "", await registry.WhoAmIAsync(registryUrl, ct));

        var registration = await registry.RegisterAsync(registryUrl, identity.SignDns("register", ipv4, ipv6, port), ct);
        WriteOverlay(config, EnabledValues(config, registration.Hostname, ipv4, ipv6,
            explicitAddress ? false : config.GetBoolean("dns.auto_address"), email, enabled));
    }

    public async Task DisableAsync(EffectiveConfig config, CancellationToken ct)
    {
        using var lease = Acquire(config);
        config = Reload(config);
        if (!config.GetBoolean("dns.enabled")) return;
        EnsureWritable(config);
        var identity = TunnelIdentity.LoadExisting(TunnelManager.IdentityPath(config))
            ?? throw new InvalidOperationException("The original identity key is missing; restore it before disabling.");
        using (identity)
        {
            if (!config.GetString("dns.hostname").StartsWith(identity.Name + ".", StringComparison.Ordinal))
                throw new InvalidOperationException("The identity key does not match the configured DNS name.");
            await registry.DeleteAsync(config.GetString("dns.registry"), identity.SignDns("delete", "", "", ProofPort(config)), ct);
        }
        WriteOverlay(config, new Dictionary<string, object>
        {
            ["dns.enabled"] = false,
            ["dns.registry"] = config.GetString("dns.registry"),
            ["dns.auto_address"] = config.GetBoolean("dns.auto_address")
        });
    }

    /// <summary>Re-publishes the name when the detected public address changed. Returns true when an update was sent.</summary>
    public async Task<bool> RefreshAddressAsync(EffectiveConfig config, CancellationToken ct)
    {
        using var lease = Acquire(config);
        config = Reload(config);
        if (!config.GetBoolean("dns.enabled") || !config.GetBoolean("dns.auto_address")) return false;
        EnsureWritable(config);
        var registryUrl = config.GetString("dns.registry");
        var current = (config.GetString("dns.ipv4"), config.GetString("dns.ipv6"));
        var next = WithAddress(current.Item1, current.Item2, await registry.WhoAmIAsync(registryUrl, ct));
        if (next == current) return false;
        var identity = TunnelIdentity.LoadExisting(TunnelManager.IdentityPath(config))
            ?? throw new InvalidOperationException("The identity key is missing; the DNS name cannot be updated.");
        using (identity)
        {
            var registration = await registry.RegisterAsync(registryUrl, identity.SignDns("update", next.Item1, next.Item2, ProofPort(config)), ct);
            WriteOverlay(config, EnabledValues(config, registration.Hostname, next.Item1, next.Item2, true,
                config.GetString("tls.acme_email"), alreadyEnabled: true));
        }
        return true;
    }

    private static Dictionary<string, object> EnabledValues(EffectiveConfig config, string hostname, string ipv4, string ipv6,
        bool autoAddress, string email, bool alreadyEnabled) => new()
    {
        ["server.public_url"] = $"https://{hostname}",
        ["tls.mode"] = "acme",
        ["tls.acme_domain"] = hostname,
        ["tls.acme_email"] = email,
        ["tls.acme_accept_terms"] = true,
        ["dns.enabled"] = true,
        ["dns.registry"] = config.GetString("dns.registry"),
        ["dns.hostname"] = hostname,
        ["dns.ipv4"] = ipv4,
        ["dns.ipv6"] = ipv6,
        ["dns.auto_address"] = autoAddress,
        ["dns.previous_public_url"] = alreadyEnabled ? config.GetString("dns.previous_public_url") : config.GetString("server.public_url"),
        ["dns.previous_tls_mode"] = alreadyEnabled ? config.GetString("dns.previous_tls_mode") : config.GetString("tls.mode"),
        ["dns.previous_acme_domain"] = alreadyEnabled ? config.GetString("dns.previous_acme_domain") : config.GetString("tls.acme_domain")
    };

    /// <summary>Replaces the slot matching the detected address family, keeping the other family as configured.</summary>
    private static (string Ipv4, string Ipv6) WithAddress(string ipv4, string ipv6, IPAddress detected)
        => detected.AddressFamily == AddressFamily.InterNetwork ? (detected.ToString(), ipv6)
            : detected.AddressFamily == AddressFamily.InterNetworkV6 ? (ipv4, detected.ToString())
            : throw new InvalidDataException("The registry returned an unsupported address family.");

    private static bool IsEmail(string value)
        => value.Length is > 2 and <= 254 && value.IndexOf('@') is > 0 and var at && at < value.Length - 1 && !value.Any(char.IsWhiteSpace);

    /// <summary>The registry checks ownership on the public HTTP listener, so it must not be loopback-only.</summary>
    private static int ProofPort(EffectiveConfig config)
    {
        if (!ListenAddress.TryParse(config.GetString("server.listen"), out var listen))
            throw new InvalidOperationException("Invalid server.listen.");
        if (listen.IsLocalhost || listen.Address is null || IPAddress.IsLoopback(listen.Address))
            throw new InvalidOperationException("server.listen is loopback-only. Set it to a public bind such as 0.0.0.0:8080 so the registry can verify ownership on the HTTP port.");
        return listen.Port;
    }

    private static EffectiveConfig Reload(EffectiveConfig config)
    {
        var fresh = ConfigLoader.Load(config.ConfigDirectory, config.DataDirectory);
        if (!fresh.IsValid) throw new InvalidOperationException("Configuration is invalid: " + string.Join("; ", fresh.Issues));
        return fresh;
    }

    private static void EnsureWritable(EffectiveConfig config)
    {
        foreach (var key in ManagedKeys.Concat(SettingDefinitions.All
                     .Where(d => d.Key.StartsWith("dns.", StringComparison.Ordinal) && d.Key != "dns.registry").Select(d => d.Key)))
        {
            var value = config.Values[key];
            if (value.Source is SettingSource.Environment or SettingSource.Flag
                || value.Source == SettingSource.ConfD && value.Origin is { } origin
                && string.CompareOrdinal(Path.GetFileName(origin[..origin.LastIndexOf(':')]), ManagedFile) > 0)
                throw new InvalidOperationException($"Remove the overriding {key} setting before changing DNS state.");
        }
    }

    private static FileStream Acquire(EffectiveConfig config)
    {
        Directory.CreateDirectory(config.ConfigDirectory);
        try { return new FileStream(Path.Combine(config.ConfigDirectory, ".dns.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another DNS operation is in progress."); }
    }

    private static void WriteOverlay(EffectiveConfig config, IReadOnlyDictionary<string, object> values)
    {
        var text = new StringBuilder("# Managed atomically by sbox-ns dns. Use dns enable/disable to change lifecycle.\n");
        foreach (var group in values.GroupBy(pair => pair.Key[..pair.Key.LastIndexOf('.')]))
        {
            text.AppendLine($"[{group.Key}]");
            foreach (var pair in group)
                text.AppendLine($"{pair.Key[(pair.Key.LastIndexOf('.') + 1)..]} = {ConfigFiles.FormatValue(pair.Value)}");
        }
        ConfigFiles.WriteAtomically(OverlayPath(config), text.ToString());
    }
}
