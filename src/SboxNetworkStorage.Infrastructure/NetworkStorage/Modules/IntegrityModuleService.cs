using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Storage;
namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>
/// Sends integrity alerts through the existing operator alert channels.
/// Implemented in the Server project over the shared throttle and dispatcher
/// so module alerts respect the same suppression as every other alert.
/// </summary>
public interface IIntegrityAlertSink
{
    Task NotifyFlagAsync(string projectId, string detector, string severity, string steamId, string summary, CancellationToken ct);
}

/// <summary>Owner id for workspace reads done off the request path. Registered in DI.</summary>
public sealed record ModuleOwnerOptions(long OwnerUserId);

/// <summary>
/// The integrity module's hook subscriber. Runs detectors on data-plane hook
/// events for projects where the module is installed and enabled, persists
/// flags, and applies the configured automatic action (flag only by default).
/// Detection never alters game results: flag-only requests complete untouched.
/// </summary>
public sealed class IntegrityModuleService(
    IModuleDataStore modules,
    IServiceScopeFactory scopes,
    IntegrityDetectors detectors,
    PlayerBlockService blocks,
    IIntegrityAlertSink alerts,
    ModuleOwnerOptions owner,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<IntegrityModuleService> logger) : IModuleHookSubscriber
{
    // Singleton (hook drain must not depend on scoped services): workspace
    // access goes through a scope, amortized by the 30-second settings cache.
    public IReadOnlyList<string> Hooks => ["endpoint.executed", "ledger.delta", "request.rejected", "revision.reported"];

    public async Task OnEventAsync(ModuleHookEvent hookEvent, CancellationToken ct)
    {
        var settings = await SettingsForAsync(hookEvent.ProjectId, ct);
        if (settings is null) return;
        DetectorFiring? firing;
        try
        {
            firing = detectors.Observe(hookEvent, settings.Thresholds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Integrity detector failed for project {ProjectId}", hookEvent.ProjectId);
            return;
        }
        if (firing is null) return;
        if (!settings.Permissions.Contains("flag-players"))
        {
            logger.LogDebug("Integrity flag for project {ProjectId} dropped: flag-players was revoked.", hookEvent.ProjectId);
            return;
        }
        var at = time.GetUtcNow();
        var flagId = $"flag_{Guid.NewGuid():N}"[..21];
        var evidence = new Dictionary<string, object?>(firing.Evidence, StringComparer.Ordinal)
        {
            ["projectId"] = hookEvent.ProjectId,
            ["endpoint"] = hookEvent.EndpointSlug,
            ["revisionId"] = hookEvent.RevisionId,
        };
        if (settings.ResponseMode == IntegrityResponseEvaluator.ModeDryRun)
        {
            var wouldApply = await WouldApplyAsync(hookEvent, settings, firing, at, ct);
            if (wouldApply is not null) evidence["wouldApply"] = wouldApply;
        }
        try
        {
            await modules.InsertFlagAsync(hookEvent.ProjectId, flagId, at.ToUnixTimeMilliseconds(),
                hookEvent.SteamId ?? "unknown", firing.Detector, firing.Severity, "open",
                note: null, JsonSerializer.SerializeToElement(evidence), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Storing integrity flag failed for project {ProjectId}", hookEvent.ProjectId);
            return;
        }
        await ApplyAutomaticActionAsync(hookEvent, settings, firing, flagId, ct);
    }

    private async Task ApplyAutomaticActionAsync(ModuleHookEvent hookEvent, IntegritySettings settings,
        DetectorFiring firing, string flagId, CancellationToken ct)
    {
        if (!settings.AutoActions.TryGetValue(firing.Detector, out var action))
            action = settings.DefaultAutoAction;
        switch (action)
        {
            case "flag-and-alert" when settings.Permissions.Contains("send-alerts"):
                try
                {
                    await alerts.NotifyFlagAsync(hookEvent.ProjectId, firing.Detector, firing.Severity,
                        hookEvent.SteamId ?? "unknown",
                        $"Integrity flag {flagId}: {firing.Detector} for player {hookEvent.SteamId}.", ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Integrity alert failed for project {ProjectId}", hookEvent.ProjectId);
                }
                break;
            case "flag-and-block-writes" when settings.Permissions.Contains("block-requests"):
                try
                {
                    var expiresAt = time.GetUtcNow().AddMinutes(settings.BlockDurationMinutes).ToUnixTimeMilliseconds();
                    await blocks.BlockAsync(hookEvent.ProjectId, hookEvent.SteamId ?? "unknown",
                        PlayerBlockService.WritesScope,
                        $"Automatic {settings.BlockDurationMinutes}-minute write block after {firing.Detector} ({flagId}).",
                        expiresAt, "integrity-module", ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Automatic integrity block failed for project {ProjectId}", hookEvent.ProjectId);
                }
                break;
            default:
                if (action != "flag-only")
                    logger.LogDebug("Integrity action '{Action}' for project {ProjectId} needs a revoked permission; kept flag only.",
                        action, hookEvent.ProjectId);
                break;
        }
    }

    /// <summary>Dry-run only: the typed response decision as evidence, or null when ineligible or unavailable.</summary>
    private async Task<JsonObject?> WouldApplyAsync(ModuleHookEvent hookEvent, IntegritySettings settings,
        DetectorFiring firing, DateTimeOffset at, CancellationToken ct)
    {
        try
        {
            var window = IntegrityResponseEvaluator.WindowFor(firing.Detector, settings.Thresholds.ConflictWindow);
            var decision = await IntegrityResponseEvaluator.EvaluateAsync(modules, hookEvent.ProjectId,
                hookEvent.SteamId ?? string.Empty, firing.Detector, window, settings.ResponseLookback, at, ct);
            if (decision.Eligibility != ResponseEligibility.Eligible) return null;
            return IntegrityResponseEvaluator.BuildWouldApply(decision, firing.Detector, hookEvent.Hook, window);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Integrity response evaluation failed for project {ProjectId}", hookEvent.ProjectId);
            return null;
        }
    }

    /// <summary>Effective integrity settings, or null when the module is not installed and enabled.</summary>
    public async Task<IntegritySettings?> SettingsForAsync(string projectId, CancellationToken ct)
    {
        var cacheKey = $"integrity-settings:{projectId}";
        if (cache.TryGetValue(cacheKey, out IntegritySettings? cached)) return cached;
        IntegritySettings? settings = null;
        try
        {
            using var scope = scopes.CreateScope();
            var states = scope.ServiceProvider.GetRequiredService<ModuleStateStore>();
            var state = await states.LoadAsync(owner.OwnerUserId, projectId, ct);
            var installed = state.Modules.FirstOrDefault(module => module.Id == "integrity" && module.Enabled);
            if (installed is not null)
                settings = IntegritySettings.From(installed.Settings, installed.Permissions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading integrity settings failed for project {ProjectId}", projectId);
        }
        cache.Set(cacheKey, settings, TimeSpan.FromSeconds(30));
        return settings;
    }

    public void InvalidateSettings(string projectId) => cache.Remove($"integrity-settings:{projectId}");
}

/// <summary>Effective per-project integrity settings (catalog defaults overlaid with saved values).</summary>
public sealed record IntegritySettings(
    IntegrityDetectors.DetectorThresholds Thresholds,
    string DefaultAutoAction,
    IReadOnlyDictionary<string, string> AutoActions,
    int BlockDurationMinutes,
    IReadOnlySet<string> Permissions,
    string ResponseMode,
    TimeSpan ResponseLookback)
{
    public static IntegritySettings From(IReadOnlyDictionary<string, string> saved, IReadOnlyList<string> permissions)
    {
        var watched = saved.TryGetValue("watched_fields", out var fields) && !string.IsNullOrWhiteSpace(fields)
            ? fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["gold", "xp", "score"];
        var thresholds = new IntegrityDetectors.DetectorThresholds(
            GetDouble(saved, "value_delta_per_minute", IntegrityDetectors.Defaults.ValueDeltaPerMinute),
            GetInt(saved, "max_calls_per_minute", IntegrityDetectors.Defaults.MaxCallsPerMinute),
            new HashSet<string>(watched, StringComparer.Ordinal));
        var auto = saved.TryGetValue("auto_action", out var action) ? action : "flag-only";
        if (auto is not ("flag-only" or "flag-and-alert" or "flag-and-block-writes")) auto = "flag-only";
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var detector in new[] { IntegrityDetectors.ValueDelta, IntegrityDetectors.CallRate,
                     IntegrityDetectors.RevisionTampering, IntegrityDetectors.AntiRollbackConflict, IntegrityDetectors.DirectWriteAttempt })
        {
            if (saved.TryGetValue($"auto_action.{detector}", out var per)
                && per is ("flag-only" or "flag-and-alert" or "flag-and-block-writes"))
                overrides[detector] = per;
        }
        var responseMode = saved.TryGetValue("response_mode", out var mode) && mode == IntegrityResponseEvaluator.ModeDryRun
            ? IntegrityResponseEvaluator.ModeDryRun
            : IntegrityResponseEvaluator.ModeOff;
        var lookbackHours = GetInt(saved, "response_lookback_hours", IntegrityResponseEvaluator.DefaultLookbackHours);
        if (lookbackHours is < IntegrityResponseEvaluator.MinLookbackHours or > IntegrityResponseEvaluator.MaxLookbackHours)
            lookbackHours = IntegrityResponseEvaluator.DefaultLookbackHours;
        return new IntegritySettings(thresholds, auto, overrides,
            Math.Clamp(GetInt(saved, "block_duration_minutes", 60), 1, 60 * 24 * 30),
            new HashSet<string>(permissions, StringComparer.Ordinal),
            responseMode, TimeSpan.FromHours(lookbackHours));
    }

    private static int GetInt(IReadOnlyDictionary<string, string> saved, string key, int fallback)
        => saved.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : fallback;

    private static double GetDouble(IReadOnlyDictionary<string, string> saved, string key, double fallback)
        => saved.TryGetValue(key, out var raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
