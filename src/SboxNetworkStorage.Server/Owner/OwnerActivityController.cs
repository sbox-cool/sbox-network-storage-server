using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerActivityTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);
public sealed record OwnerActivityModel(string ProjectId, string ProjectName, string Tab, string Month, string Date,
    IReadOnlyList<OwnerActivityTable> Tables);

[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerActivityController(INetworkStorageProjectService projects, INetworkStorageStore store) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;

    [HttpGet("/dashboard/projects/{projectId}/activity/{tab}")]
    public async Task<IActionResult> Activity(string projectId, string tab, [FromQuery] string? month, [FromQuery] string? date, CancellationToken ct)
    {
        if (tab is not ("analytics" or "logs" or "errors" or "usage")) return NotFound();
        var access = await projects.ResolveProjectAccessAsync(Owner, projectId, ct);
        if (access is null || access.StorageOwnerUserId != Owner || !access.CanManage) return NotFound();
        month ??= DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        date ??= DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return BadRequest("Choose a month in YYYY-MM format and an issue date in YYYY-MM-DD format.");
        var tables = new List<OwnerActivityTable>();
        switch (tab)
        {
            case "analytics":
                tables.Add(Table("Player presence", await store.ReadProjectProfilesAsync(projectId, ct), "steam_id", "player_name", "is_online", "last_seen_unix_ms", "total_seconds", "session_count", "last_event_type"));
                tables.Add(Table("Reported issues · " + date, await store.ListProjectIssuesAsync(projectId, date, 200, ct), "created_at_unix_ms", "steam_id", "category", "event_type", "label"));
                tables.Add(Table("Query executions", await store.ListQueryLastRunsAsync(projectId, ct), "query_id", "run_at", "duration_ms", "keys_scanned", "records_returned", "from_cache"));
                break;
            case "logs":
                tables.Add(Table("Owner audit trail · latest 200", await store.ListAuditLogsAsync(projectId, 200, ct), "created_at_unix_ms", "action", "user_id", "log_id"));
                tables.Add(Table("Runtime requests · latest 200", await store.ListStorageRequestLogAsync(projectId, 200, ct), "created_at_unix_ms", "method", "path", "status_code", "duration_ms"));
                break;
            case "errors":
                tables.Add(Table("Storage errors · latest 200", await store.ListStorageErrorsAsync(projectId, 200, ct), "created_at_unix_ms", "severity", "source", "request_path", "error_id"));
                break;
            case "usage":
                var monthly = await store.ReadProjectUsageMonthlyAsync(projectId, month, ct);
                tables.Add(Table("Monthly usage · " + month, monthly is { } row ? new[] { row } : [], "requests", "reads", "writes", "endpoint_calls", "errors", "bytes_in", "bytes_out", "compute_units"));
                tables.Add(Table("Daily usage", await store.ReadProjectUsageDailyAsync(projectId, month, ct), "day", "requests", "errors", "bytes_in", "bytes_out", "compute_units"));
                tables.Add(Table("Endpoint usage · up to 200", await store.ReadProjectUsageEndpointsAsync(projectId, month, 200, ct), "endpoint_slug", "calls", "errors", "duration_ms_sum", "compute_units"));
                break;
        }
        return View("~/Views/Owner/Activity.cshtml", new OwnerActivityModel(projectId, access.Project.Name, tab, month, date, tables));
    }

    private static OwnerActivityTable Table(string title, IReadOnlyList<JsonElement> rows, params string[] columns)
        => new(title, columns, rows.Select(row => (IReadOnlyList<string>)columns.Select(column => Display(row, column)).ToArray()).ToArray());

    private static string Display(JsonElement row, string column)
    {
        if (!row.TryGetProperty(column, out var value) || value.ValueKind == JsonValueKind.Null) return "—";
        if (column.EndsWith("_unix_ms", StringComparison.Ordinal) && value.TryGetInt64(out var milliseconds))
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "—" : value.GetRawText();
    }
}
