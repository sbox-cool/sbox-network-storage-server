using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Monitoring;
using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerServerModel(
    string Version, string Runtime, string Platform, DateTimeOffset StartedAt, string Database, string? DatabaseVersion,
    double? DatabaseLatencyMs, string DataDirectory, string Scope, double CpuCores, long? MemoryLimit, int RuleCount,
    HostSample? Latest, SlowInfo Slow, OwnerUpdateHealth Update);

/// <summary>This build's post-update watch: its state, and the live per-library verdict when the database answers.</summary>
public sealed record OwnerUpdateHealth(string Version, string Watch, string Detail, PostUpdateVerdict? Verdict);

public sealed record OwnerProjectOption(string Id, string Name);

public sealed record OwnerProviderView(
    string Id,
    string DisplayName,
    string OriginLabel,
    string Capabilities,
    string Description,
    bool Available,
    string? UnavailableReason,
    bool Configured,
    string? Detail,
    string? FromAddress,
    IReadOnlyList<string> Created,
    bool HasSecret);

public sealed record OwnerAlertsModel(
    IReadOnlyList<ChannelInfo> Channels,
    IReadOnlyList<AlertRule> Rules,
    IReadOnlyDictionary<string, RuleStatus> Statuses,
    IReadOnlyList<MonitoringEvent> History,
    IReadOnlyList<OwnerProjectOption> Projects,
    AlertRule? EditingRule,
    ChannelDefinition? EditingChannel,
    bool EditingChannelHasSecret,
    string? RuleError = null,
    string? ChannelError = null,
    IReadOnlyList<ChannelDelivery>? TestResult = null,
    IReadOnlyList<OwnerProviderView>? Providers = null,
    ProviderPlan? PendingPlan = null,
    ProviderInputs? PendingInputs = null,
    bool PendingHasToken = false,
    string? PendingProviderId = null,
    string? ProviderError = null,
    string? ProviderNotice = null);

/// <summary>
/// Server-wide operator pages: <c>/dashboard/server</c> (live host stats, short history) and
/// <c>/dashboard/server/alerts</c> (alert rules, notification channels, history). Owner only;
/// the public demo renders generated examples without reading host measurements or channel secrets.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerServerController(
    MetricsSampler sampler,
    HostMetricsReader reader,
    MonitoringStore store,
    AlertRuleEngine engine,
    AlertDispatcher dispatcher,
    ProviderRegistry providers,
    INetworkStorageStoreAdmin admin,
    IWorkspaceStore workspace,
    SboxNetworkStorage.Server.Configuration.EffectiveConfig config,
    EndpointWriteQueue writeQueue,
    ScriptConcurrencyGate scriptGate,
    PostUpdateVerifier verifier) : Controller
{
    private const string AlertsUrl = "/dashboard/server/alerts";

    [HttpGet("/dashboard/server")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (config.GetBoolean("adminpanel.demo_read_only"))
            return View("~/Views/Owner/Server.cshtml", Demo.DemoMonitoring.Server());
        string? dbVersion = null;
        double? latency = null;
        try
        {
            var ping = await admin.PingAsync(ct);
            dbVersion = ping.ServerVersion;
            latency = Math.Round(ping.RoundTrip.TotalMilliseconds, 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The page still shows host stats while the database is down.
        }

        // Rendering the page counts as watching it, so a refresh without JavaScript shows fresh values.
        sampler.Watch("page-render", MetricsSampler.DefaultInterval);
        return View("~/Views/Owner/Server.cshtml", new OwnerServerModel(
            MetricsSampler.Version, MetricsSampler.Runtime, MetricsSampler.Platform, sampler.StartedAt,
            admin.ProviderName, dbVersion, latency, Path.GetFullPath(config.DataDirectory), reader.Scope, reader.CpuCores,
            reader.MemoryLimitBytes, store.Rules.Count(r => r.Enabled), sampler.Latest, sampler.Slow, await UpdateHealthAsync(ct)));
    }

    /// <summary>The post-update watch for this build and its live per-library verdict. The watch state works without the database.</summary>
    private async Task<OwnerUpdateHealth> UpdateHealthAsync(CancellationToken ct)
    {
        var version = BuildInfo.Version;
        var marker = UpdateWatchService.ReadMarkerFile(config.StateDirectory);
        if (marker is null || marker.Version != version)
        {
            return new(version, "Not watching", "No post-update watch has started for this build yet.", null);
        }

        var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(marker.SinceUnixMs);
        var windowHours = PostUpdateVerifier.WatchWindowHours;
        var alerted = marker.AlertedAtUnixMs is not null;
        if (!alerted && elapsed > TimeSpan.FromHours(windowHours))
        {
            return new(version, "Watch ended", $"No library regression alert in the {windowHours} h after the update.", null);
        }

        PostUpdateVerdict? verdict = null;
        try
        {
            verdict = await verifier.VerifyAsync(version, Math.Clamp((int)Math.Ceiling(elapsed.TotalHours), 1, windowHours), VerdictThresholds.Default, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The verdict needs the database; the watch state above does not.
        }

        return alerted
            ? new(version, "Regression", $"A library regression alert was sent at {DateTimeOffset.FromUnixTimeMilliseconds(marker.AlertedAtUnixMs!.Value):u}. Confirm it with sbox-ns update verify, then roll back as shown below.", verdict)
            : new(version, "Watching", $"{Math.Floor(elapsed.TotalHours)} of {windowHours} h since this build started.", verdict);
    }

    /// <summary>
    /// Live samples for the page. Each poll leases the requested interval for a few seconds; the
    /// shared sampler runs at the fastest leased interval and stops when no one polls.
    /// </summary>
    [HttpGet("/dashboard/server/stats")]
    public IActionResult Stats([FromQuery] string? viewer, [FromQuery] int interval = 500, [FromQuery] long after = 0)
    {
        if (string.IsNullOrEmpty(viewer) || viewer.Length > 64) return BadRequest();
        if (config.GetBoolean("adminpanel.demo_read_only"))
            return Json(Demo.DemoMonitoring.Stats(interval, after));
        var granted = sampler.Watch(viewer, TimeSpan.FromMilliseconds(interval));
        var slow = sampler.Slow;
        return Json(new
        {
            interval = (int)granted.TotalMilliseconds,
            samples = sampler.Since(after, 240),
            slow = new { slow.DataFree, slow.DataTotal, slow.DataSize, slow.BackupSize, newestBackup = slow.NewestBackup?.ToUnixTimeMilliseconds() },
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - sampler.StartedAt).TotalSeconds,
            writeQueue = writeQueue.GetStats(),
            scriptGate = scriptGate.GetStats(),
        });
    }

    /// <summary>History for the charts: <c>15m</c> full resolution, <c>24h</c> one-minute rollups.</summary>
    [HttpGet("/dashboard/server/history")]
    public IActionResult History([FromQuery] string? range)
        => config.GetBoolean("adminpanel.demo_read_only")
            ? Json(Demo.DemoMonitoring.History(range))
            : range == "24h"
                ? Json(new { range = "24h", rollups = sampler.Rollups() })
                : Json(new { range = "15m", samples = sampler.Recent(MetricsSampler.LiveRetention) });

    [HttpGet(AlertsUrl)]
    public async Task<IActionResult> Alerts([FromQuery] string? rule, [FromQuery] string? channel, CancellationToken ct)
    {
        if (config.GetBoolean("adminpanel.demo_read_only"))
            return View("~/Views/Owner/Alerts.cshtml", Demo.DemoMonitoring.Alerts(await ProjectsAsync(ct)));
        var editingChannel = store.Channels.FirstOrDefault(c => c.Id == channel);
        return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(
            store.Rules.FirstOrDefault(r => r.Id == rule), editingChannel, editingChannel is not null && store.HasSecret(editingChannel.Id), ct));
    }

    [HttpPost(AlertsUrl + "/rules")]
    public async Task<IActionResult> SaveRule([FromForm] RuleForm form, CancellationToken ct)
    {
        var existing = form.Id is null ? null : store.Rules.FirstOrDefault(r => r.Id == form.Id);
        if (form.Id is not null && existing is null) return NotFound();
        var (rule, error) = form.ToRule(existing?.Id ?? NewId("r"), dispatcher.Channels());
        if (error is not null)
        {
            Response.StatusCode = 400;
            return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(rule, null, false, ct) with { RuleError = error });
        }

        store.SaveRule(rule);
        OwnerFlash.Success(this, $"Rule {rule.Name} saved.");
        return Redirect(AlertsUrl + "#rules");
    }

    [HttpPost(AlertsUrl + "/rules/delete")]
    public IActionResult DeleteRule([FromForm] string? id)
    {
        var rule = store.Rules.FirstOrDefault(r => r.Id == id);
        if (rule is null || !store.DeleteRule(rule.Id)) return NotFound();
        OwnerFlash.Success(this, $"Rule {rule.Name} deleted.");
        return Redirect(AlertsUrl + "#rules");
    }

    [HttpPost(AlertsUrl + "/channels")]
    public async Task<IActionResult> SaveChannel([FromForm] ChannelForm form, CancellationToken ct)
    {
        var existing = form.Id is null ? null : store.Channels.FirstOrDefault(c => c.Id == form.Id);
        if (form.Id is not null && existing is null) return NotFound();
        var projects = await ProjectsAsync(ct);
        var id = existing?.Id ?? NewId("c");
        var (channel, secrets, error) = form.ToChannel(id, existing is null ? null : store.ReadSecrets(id), projects);
        if (error is not null)
        {
            Response.StatusCode = 400;
            return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, channel, existing is not null && store.HasSecret(id), ct) with { ChannelError = error });
        }

        store.SaveChannel(channel, secrets);
        OwnerFlash.Success(this, $"Channel {channel.Name} saved.");
        return Redirect(AlertsUrl + "#channels");
    }

    [HttpPost(AlertsUrl + "/channels/delete")]
    public IActionResult DeleteChannel([FromForm] string? id)
    {
        var channel = store.Channels.FirstOrDefault(c => c.Id == id);
        if (channel is null || !store.DeleteChannel(channel.Id)) return NotFound();
        OwnerFlash.Success(this, $"Channel {channel.Name} deleted.");
        return Redirect(AlertsUrl + "#channels");
    }

    /// <summary>Sends a real test notification and shows the outcome inline (errors stay next to their cause).</summary>
    [HttpPost(AlertsUrl + "/channels/test")]
    public async Task<IActionResult> TestChannel([FromForm] string? id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id)) return NotFound();
        var delivery = await dispatcher.SendTestAsync(id, ct);
        if (delivery is null) return NotFound();
        return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, null, false, ct) with { TestResult = [delivery] });
    }

    /// <summary>Shows exactly what a provider setup will create. Creates nothing and needs no network.</summary>
    [HttpPost(AlertsUrl + "/providers/plan")]
    public async Task<IActionResult> PlanProvider([FromForm] ProviderSetupForm form, CancellationToken ct)
    {
        var provider = providers.Find(form.ProviderId ?? "");
        if (provider is null) return NotFound();
        if (provider.CheckAvailability() is { Available: false, Reason: { } reason })
            return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, null, false, ct) with { ProviderError = reason });
        var inputs = form.ToInputs();
        if (provider.ValidateInputs(inputs) is { } error)
        {
            Response.StatusCode = 400;
            return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, null, false, ct)
                with { ProviderError = error, PendingInputs = form.Redacted(), PendingProviderId = provider.Id });
        }

        var plan = await provider.PlanSetupAsync(inputs, ct);
        return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, null, false, ct)
            with
            {
                PendingPlan = plan, PendingInputs = form.Redacted(), PendingHasToken = inputs.Token.Length > 0,
                PendingProviderId = provider.Id,
                ProviderNotice = "Review what will be created, then enter the token again to confirm. Tokens are never shown back.",
            });
    }

    /// <summary>Validates credentials, creates the planned resources, and adds a provider channel.</summary>
    [HttpPost(AlertsUrl + "/providers/apply")]
    public async Task<IActionResult> ApplyProvider([FromForm] ProviderSetupForm form, CancellationToken ct)
    {
        var provider = providers.Find(form.ProviderId ?? "");
        if (provider is null) return NotFound();
        if (provider.CheckAvailability() is { Available: false, Reason: { } reason })
            return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, null, false, ct) with { ProviderError = reason });
        var stored = store.ReadProviderSecrets(provider.Id);
        var inputs = form.ToInputs() with { Token = form.ToInputs().Token.Length > 0 ? form.ToInputs().Token : stored.Token ?? "" };
        if (provider.ValidateInputs(inputs) is { } error)
        {
            Response.StatusCode = 400;
            return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, null, false, ct)
                with { ProviderError = error, PendingInputs = form.Redacted(), PendingProviderId = provider.Id });
        }

        ProviderApplyResult result;
        try
        {
            result = await provider.ApplySetupAsync(inputs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Response.StatusCode = 502;
            return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, null, false, ct)
                with { ProviderError = "Setup failed unexpectedly; see the server log.", PendingInputs = form.Redacted(), PendingProviderId = provider.Id });
        }

        if (result.Created.Count > 0)
        {
            store.SaveProviderSetup(new ProviderSetup(provider.Id, result.Ok,
                inputs.AccountId, inputs.ZoneId, inputs.Subdomain, result.FromAddress ?? inputs.FromAddress,
                inputs.ForwardTo.Length > 0, result.Created, result.Detail ?? result.Error, DateTimeOffset.UtcNow),
                new ProviderSecrets(Token: inputs.Token, AccountId: inputs.AccountId, ZoneId: inputs.ZoneId,
                    Subdomain: inputs.Subdomain, FromAddress: result.FromAddress ?? inputs.FromAddress, ForwardTo: inputs.ForwardTo));
            EnsureProviderChannel(provider);
        }

        if (!result.Ok)
        {
            Response.StatusCode = 502;
            return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, null, false, ct)
                with
                {
                    ProviderError = result.Error ?? "Setup failed.",
                    ProviderNotice = result.Created.Count == 0 ? null : "Some resources were created before the failure; tearing down removes them.",
                    PendingInputs = form.Redacted(),
                    PendingProviderId = provider.Id,
                });
        }

        OwnerFlash.Success(this, $"{provider.DisplayName} is set up.");
        return Redirect(AlertsUrl + "#providers");
    }

    /// <summary>Re-checks a configured provider and stores the status text.</summary>
    [HttpPost(AlertsUrl + "/providers/verify")]
    public async Task<IActionResult> VerifyProvider([FromForm] string? id, CancellationToken ct)
    {
        var provider = providers.Find(id ?? "");
        var setup = provider is null ? null : store.GetProviderSetup(provider.Id);
        if (provider is null || setup is null) return NotFound();
        ProviderVerifyResult result;
        try
        {
            result = await provider.VerifyAsync(ProviderContext(setup, store.ReadProviderSecrets(provider.Id)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ProviderVerifyResult(false, "The check failed unexpectedly; see the server log.");
        }

        store.SaveProviderSetup(setup with { Detail = result.Detail, UpdatedAt = DateTimeOffset.UtcNow }, secrets: null);
        OwnerFlash.Success(this, result.Ok ? $"{provider.DisplayName}: {result.Detail}" : $"{provider.DisplayName} check failed: {result.Detail}");
        return Redirect(AlertsUrl + "#providers");
    }

    /// <summary>Deletes only the recorded provider resources, then forgets the setup and its secret.</summary>
    [HttpPost(AlertsUrl + "/providers/teardown")]
    public async Task<IActionResult> TeardownProvider([FromForm] string? id, CancellationToken ct)
    {
        var provider = providers.Find(id ?? "");
        var setup = provider is null ? null : store.GetProviderSetup(provider.Id);
        if (provider is null || setup is null) return NotFound();
        ProviderTeardownResult result;
        try
        {
            result = await provider.TeardownAsync(
                new ProviderTeardownContext(ProviderContext(setup, store.ReadProviderSecrets(provider.Id)), setup.Created), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Response.StatusCode = 502;
            return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, null, false, ct)
                with { ProviderError = "Teardown failed unexpectedly; see the server log." });
        }

        if (!result.Ok)
        {
            Response.StatusCode = 502;
            return View("~/Views/Owner/Alerts.cshtml", await ModelAsync(null, null, false, ct) with { ProviderError = result.Error });
        }

        store.DeleteProviderSetup(provider.Id);
        OwnerFlash.Success(this, $"{provider.DisplayName} removed ({result.Removed} resource(s)). Its channel stays but cannot send until set up again.");
        return Redirect(AlertsUrl + "#providers");
    }

    private ProviderSendContext ProviderContext(ProviderSetup setup, ProviderSecrets secrets) => new(
        setup.AccountId, setup.ZoneId, setup.Subdomain, setup.FromAddress, secrets.ForwardTo ?? "", secrets.Token ?? "");

    private void EnsureProviderChannel(IAlertProvider provider)
    {
        if (store.Channels.Any(c => c.Kind == provider.Id)) return;
        store.SaveChannel(new ChannelDefinition(NewId("c"), provider.DisplayName, provider.Id,
            "generic", AlertSeverity.Info, null, true), secrets: null);
    }

    private async Task<OwnerAlertsModel> ModelAsync(AlertRule? rule, ChannelDefinition? channel, bool channelHasSecret, CancellationToken ct)
    {
        var statuses = new Dictionary<string, RuleStatus>(StringComparer.Ordinal);
        foreach (var r in store.Rules)
            if (engine.Status(r.Id) is { } status) statuses[r.Id] = status;
        return new OwnerAlertsModel(dispatcher.Channels(), store.Rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            statuses, store.History.Take(100).ToList(), await ProjectsAsync(ct), rule, channel, channelHasSecret,
            Providers: providers.All.Select(ViewOf).ToList());
    }

    private OwnerProviderView ViewOf(IAlertProvider provider)
    {
        var (available, reason) = provider.CheckAvailability();
        var setup = store.GetProviderSetup(provider.Id);
        return new OwnerProviderView(provider.Id, provider.DisplayName, provider.OriginLabel, provider.CapabilitiesLabel,
            provider.Description, available, reason, setup?.Configured == true, setup?.Detail, setup?.FromAddress,
            setup?.Created.Select(c => c.Description).ToList() ?? [], store.HasProviderSecret(provider.Id));
    }

    private async Task<IReadOnlyList<OwnerProjectOption>> ProjectsAsync(CancellationToken ct)
        => (await workspace.GetUserProjectsAsync(OwnerProjectScope.Owner, ct))
            .Select(p => new OwnerProjectOption(p.Id, p.Name))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NewId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    public sealed class RuleForm
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Metric { get; set; }
        public string? Comparison { get; set; }
        public string? Threshold { get; set; }
        public int ForSeconds { get; set; }
        public string? Severity { get; set; }
        public List<string>? Channels { get; set; }
        public bool Enabled { get; set; }
        public int CooldownMinutes { get; set; } = 10;

        public (AlertRule Rule, string? Error) ToRule(string id, IReadOnlyList<ChannelInfo> channels)
        {
            var name = (Name ?? "").Trim();
            var metric = MonitoringMetrics.Find(Metric);
            AlertMessage.TryParseSeverity(Severity, out var severity);
            var thresholdOk = double.TryParse(Threshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) && double.IsFinite(threshold);
            var chosen = (Channels ?? []).Where(c => channels.Any(known => known.Id == c)).Distinct().ToList();
            var rule = new AlertRule(id, name, Metric ?? "", Comparison ?? ">", thresholdOk ? threshold : 0, ForSeconds, severity, chosen, Enabled, CooldownMinutes);
            string? error = null;
            if (name.Length is 0 or > 80) error = "Enter a rule name of 1-80 characters.";
            else if (metric is null) error = "Choose a metric.";
            else if (metric.Kind != MetricKind.Event && !MonitoringMetrics.Comparisons.Contains(Comparison ?? "")) error = "Choose a comparison.";
            else if (metric.Kind != MetricKind.Event && !thresholdOk) error = "Enter the threshold as a number, for example 90 or 2.5.";
            else if (ForSeconds is < 0 or > 86_400) error = "The duration must be 0-86400 seconds.";
            else if (metric.Kind == MetricKind.Window && ForSeconds < 60) error = "Request metrics need a window of at least 60 seconds.";
            else if (CooldownMinutes is < 0 or > 10_080) error = "The cooldown must be 0-10080 minutes.";
            else if (!AlertMessage.TryParseSeverity(Severity, out _)) error = "Choose a severity.";
            return (rule, error);
        }
    }

    public sealed class ChannelForm
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? Preset { get; set; }
        public string? MinSeverity { get; set; }
        public string? ProjectId { get; set; }
        public bool Enabled { get; set; }
        public string? Url { get; set; }
        public string? SigningSecret { get; set; }
        public bool ClearSigningSecret { get; set; }
        public string? DiscordUsername { get; set; }
        public string? SmtpHost { get; set; }
        public int SmtpPort { get; set; } = 587;
        public bool SmtpUseTls { get; set; }
        public string? SmtpUsername { get; set; }
        public string? SmtpPassword { get; set; }
        public string? SmtpFrom { get; set; }
        public string? SmtpTo { get; set; }

        public (ChannelDefinition Channel, ChannelSecrets? Secrets, string? Error) ToChannel(
            string id, ChannelSecrets? stored, IReadOnlyList<OwnerProjectOption> projects)
        {
            var name = (Name ?? "").Trim();
            var kind = Kind is "smtp" or "discord" or "webhook" ? Kind : "";
            var preset = kind == "webhook" && WebhookChannel.Presets.Contains(Preset ?? "") ? Preset! : "generic";
            AlertMessage.TryParseSeverity(MinSeverity, out var severity);
            var project = string.IsNullOrEmpty(ProjectId) ? null : ProjectId;
            var to = (SmtpTo ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var channel = new ChannelDefinition(id, name, kind, preset, severity, project, Enabled,
                SmtpHost: (SmtpHost ?? "").Trim(), SmtpPort: SmtpPort, SmtpUseTls: SmtpUseTls, SmtpUsername: (SmtpUsername ?? "").Trim(),
                SmtpFrom: (SmtpFrom ?? "").Trim(), SmtpTo: to,
                DiscordUsername: string.IsNullOrWhiteSpace(DiscordUsername) ? "sbox-ns" : DiscordUsername.Trim());

            // Blank secret fields keep the stored value; secrets are never sent back to the browser.
            var url = string.IsNullOrWhiteSpace(Url) ? stored?.Url : Url.Trim();
            var password = string.IsNullOrEmpty(SmtpPassword) ? stored?.Password : SmtpPassword;
            var signing = ClearSigningSecret ? null : string.IsNullOrEmpty(SigningSecret) ? stored?.SigningSecret : SigningSecret;
            var secrets = kind switch
            {
                "smtp" => new ChannelSecrets(Password: password),
                "discord" => new ChannelSecrets(Url: url),
                _ => new ChannelSecrets(Url: url, SigningSecret: signing),
            };

            string? error = null;
            if (name.Length is 0 or > 80) error = "Enter a channel name of 1-80 characters.";
            else if (kind.Length == 0) error = "Choose email, Discord or webhook.";
            else if (!AlertMessage.TryParseSeverity(MinSeverity, out _)) error = "Choose a minimum severity.";
            else if (project is not null && !projects.Any(p => p.Id == project)) error = "Choose an existing project or all projects.";
            else if (kind == "smtp") error = SmtpError(channel);
            else if (kind == "discord" && !(Uri.TryCreate(url, UriKind.Absolute, out var discord) && discord.Scheme == Uri.UriSchemeHttps))
                error = "Enter the Discord webhook URL (https://).";
            else if (kind == "discord" && Uri.TryCreate(url, UriKind.Absolute, out var discordUrl) && AlertWebhookUrlPolicy.UnsafeReason(discordUrl) is { } discordUnsafe)
                error = discordUnsafe;
            else if (kind == "webhook" && !(Uri.TryCreate(url, UriKind.Absolute, out var hook) && (hook.Scheme == Uri.UriSchemeHttps || hook.Scheme == Uri.UriSchemeHttp)))
                error = "Enter the webhook URL (http:// or https://).";
            else if (kind == "webhook" && Uri.TryCreate(url, UriKind.Absolute, out var hookUrl) && AlertWebhookUrlPolicy.UnsafeReason(hookUrl) is { } hookUnsafe)
                error = hookUnsafe;
            else if ((url?.Length ?? 0) > 2048 || (signing?.Length ?? 0) > 256 || (password?.Length ?? 0) > 1024)
                error = "The URL, signing secret or password is too long.";
            return (channel, error is null ? secrets : null, error);
        }

        private static string? SmtpError(ChannelDefinition channel)
        {
            if (channel.SmtpHost.Length is 0 or > 253 || channel.SmtpHost.Contains(' ')) return "Enter the SMTP host name.";
            if (channel.SmtpPort is < 1 or > 65_535) return "The SMTP port must be 1-65535.";
            if (!MailAddress.TryCreate(channel.SmtpFrom, out _)) return "Enter a valid from address.";
            if (channel.SmtpTo is null || channel.SmtpTo.Count is 0 or > 20 || channel.SmtpTo.Any(to => !MailAddress.TryCreate(to, out _)))
                return "Enter 1-20 valid recipient addresses, separated by commas.";
            return null;
        }
    }

    public sealed class ProviderSetupForm
    {
        public string? ProviderId { get; set; }
        public string? Token { get; set; }
        public string? AccountId { get; set; }
        public string? ZoneId { get; set; }
        public string? Subdomain { get; set; }
        public string? FromAddress { get; set; }
        public string? ForwardTo { get; set; }

        public ProviderInputs ToInputs() => new(
            (Token ?? "").Trim(), (AccountId ?? "").Trim(), (ZoneId ?? "").Trim(),
            (Subdomain ?? "").Trim(), (FromAddress ?? "").Trim(), (ForwardTo ?? "").Trim());

        /// <summary>Echoable back to the form. The token is never rendered: confirm asks for it again.</summary>
        public ProviderInputs Redacted() => ToInputs() with { Token = "" };
    }
}
