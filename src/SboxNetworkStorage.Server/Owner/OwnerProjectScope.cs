using System.Globalization;
using System.Text.Json;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>Shared lookups for the owner management pages (rate limits, webhooks, tests, versions, pages).</summary>
internal static class OwnerProjectScope
{
    public const long Owner = NetworkStorageServices.LocalOwnerUserId;

    /// <summary>The project when the local owner may manage it; otherwise null (callers answer 404).</summary>
    public static async Task<WorkspaceProject?> ResolveAsync(INetworkStorageProjectService projects, string projectId, CancellationToken ct)
    {
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        return access is null || access.StorageOwnerUserId != Owner || !access.CanManage ? null : access.Project;
    }

    public static string ProjectUrl(string projectId) => $"/dashboard/projects/{Uri.EscapeDataString(projectId)}";

    public static Task AuditAsync(IAuditLogger audit, string projectId, string action, object summary, CancellationToken ct)
        => audit.LogActionAsync(new AuditLogRequest(ProjectId: projectId, UserId: Owner.ToString(CultureInfo.InvariantCulture),
            Action: action, Actor: new { id = Owner, type = "owner-dashboard" }, Target: new { id = projectId, type = "project" },
            Summary: summary, Before: null, After: null), ct);

    public static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A store column that may hold JSON as an object or as encoded text.</summary>
    public static JsonElement? JsonColumn(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind != JsonValueKind.String) return value.Clone();
        try
        {
            using var document = JsonDocument.Parse(value.GetString() ?? "null");
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
