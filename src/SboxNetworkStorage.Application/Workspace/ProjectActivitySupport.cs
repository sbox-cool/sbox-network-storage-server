using System.Globalization;
using System.Text.Json;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Application.Workspace;

/// <summary>
/// Mirrors services/project-activity.js: last activity is the newest timestamp across
/// project metadata, collections, endpoints, workflows, and usage.
/// </summary>
public static class ProjectActivitySupport
{
    private static readonly string[] ActivityTimestampKeys = ["updatedAt", "createdAt", "compiledAt"];

    public sealed record Snapshot(
        int CollectionCount,
        int EndpointCount,
        int WorkflowCount,
        string? LastActivityAt,
        long LastActivityMs);

    public static Snapshot Compute(
        BunnyProject project,
        string? collectionsJson,
        string? endpointsJson,
        string? workflowsJson,
        string? usageJson)
    {
        var collections = ParseResourceArray(collectionsJson);
        var endpoints = ParseResourceArray(endpointsJson);
        var workflows = ParseResourceArray(workflowsJson);
        var usage = ParseUsageRoot(usageJson);

        var lastActivityMs = ComputeLastActivityMs(project, collections, endpoints, workflows, usage);
        return new Snapshot(
            CollectionCount: collections.Count,
            EndpointCount: endpoints.Count,
            WorkflowCount: workflows.Count,
            LastActivityAt: lastActivityMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(lastActivityMs).ToUniversalTime().ToString("o")
                : null,
            LastActivityMs: lastActivityMs);
    }

    public static long ComputeLastActivityMs(
        BunnyProject project,
        IReadOnlyList<JsonElement> collections,
        IReadOnlyList<JsonElement> endpoints,
        IReadOnlyList<JsonElement> workflows,
        JsonElement? usage)
    {
        var latest = TimestampOf(project);
        latest = Math.Max(latest, MaxTimestamp(collections));
        latest = Math.Max(latest, MaxTimestamp(endpoints));
        latest = Math.Max(latest, MaxTimestamp(workflows));
        latest = Math.Max(latest, UsageLastActivityMs(usage));
        return latest;
    }

    public static IReadOnlyList<JsonElement> ParseResourceArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var items = new List<JsonElement>(document.RootElement.GetArrayLength());
            foreach (var element in document.RootElement.EnumerateArray())
            {
                items.Add(element.Clone());
            }

            return items;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static JsonElement? ParseUsageRoot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long TimestampOf(BunnyProject project)
    {
        return Math.Max(
            ToEpoch(project.UpdatedAt),
            Math.Max(ToEpoch(project.CreatedAt), ToEpoch(project.CompiledAt)));
    }

    private static long TimestampOf(JsonElement resource)
    {
        if (resource.ValueKind != JsonValueKind.Object) return 0;

        var max = 0L;
        foreach (var key in ActivityTimestampKeys)
        {
            if (!resource.TryGetProperty(key, out var value)) continue;
            max = Math.Max(max, ToEpoch(value));
        }

        return max;
    }

    private static long MaxTimestamp(IReadOnlyList<JsonElement> items)
    {
        var max = 0L;
        foreach (var item in items)
        {
            max = Math.Max(max, TimestampOf(item));
        }

        return max;
    }

    private static long UsageLastActivityMs(JsonElement? usage)
    {
        if (usage is not { } root || root.ValueKind != JsonValueKind.Object) return 0;

        var latest = 0L;
        if (root.TryGetProperty("lastActivityMs", out var lastActivityMs)
            && lastActivityMs.ValueKind == JsonValueKind.Number
            && lastActivityMs.TryGetInt64(out var ms)
            && ms > 0)
        {
            latest = ms;
        }

        latest = Math.Max(latest, ToEpoch(root, "lastActivityAt"));
        latest = Math.Max(latest, ToEpoch(root, "activityAt"));
        latest = Math.Max(latest, ToEpoch(root, "updatedAt"));
        latest = Math.Max(latest, SafeUsagePerDayTimestamp(root));
        return latest;
    }

    private static long SafeUsagePerDayTimestamp(JsonElement usage)
    {
        if (!usage.TryGetProperty("perDay", out var perDay)
            || perDay.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var latest = 0L;
        foreach (var property in perDay.EnumerateObject())
        {
            var normalized = property.Name.Trim();
            if (normalized.Length == 0) continue;

            if (normalized.Length == 10
                && DateOnly.TryParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                var candidate = DateTimeOffset.Parse($"{normalized}T23:59:59.999Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
                latest = Math.Max(latest, candidate);
                continue;
            }

            latest = Math.Max(latest, ToEpoch(property.Value));
        }

        return latest;
    }

    private static long ToEpoch(DateTimeOffset? value)
        => value?.ToUnixTimeMilliseconds() ?? 0;

    private static long ToEpoch(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) ? ToEpoch(value) : 0;

    private static long ToEpoch(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var number):
                return number;
            case JsonValueKind.String:
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text)) return 0;
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed.ToUnixTimeMilliseconds()
                    : 0;
            }
            default:
                return 0;
        }
    }
}
