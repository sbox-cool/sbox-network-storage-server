using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Activity;

namespace SboxNetworkStorage.Server.Owner;

/// <param name="Help">Cause and fix for the row's status or error code, when known.</param>
/// <param name="StackTrace">Stored stack trace (already truncated when recorded).</param>
public sealed record OwnerActivityRow(IReadOnlyList<string> Cells, string? Help = null, string? StackTrace = null);
public sealed record OwnerActivityTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<OwnerActivityRow> Rows, string Empty);
public sealed record OwnerActivityModel(string ProjectId, string ProjectName, string Tab, string Month, string Date,
    IReadOnlyList<OwnerActivityTable> Tables);

[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerActivityController(INetworkStorageProjectService projects, INetworkStorageStore store) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;
    private const string NoTraffic = "Nothing recorded yet. Rows appear here once the game sends matching requests.";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["created_at_unix_ms"] = "Time (UTC)", ["steam_id"] = "Steam ID", ["player_name"] = "Player", ["is_online"] = "Online",
        ["last_seen_unix_ms"] = "Last seen (UTC)", ["total_seconds"] = "Play time (s)", ["session_count"] = "Sessions",
        ["last_event_type"] = "Last event", ["category"] = "Category", ["event_type"] = "Event", ["label"] = "Label",
        ["query_id"] = "Query", ["run_at"] = "Run at", ["duration_ms"] = "Duration (ms)", ["keys_scanned"] = "Keys scanned",
        ["records_returned"] = "Records returned", ["from_cache"] = "From cache", ["action"] = "Action", ["user_id"] = "User",
        ["log_id"] = "Log ID", ["method"] = "Method", ["path"] = "Path", ["status_code"] = "Status", ["severity"] = "Severity",
        ["source"] = "Source", ["request_path"] = "Request or record", ["error_id"] = "Error", ["message"] = "Message",
        ["requests"] = "Requests", ["reads"] = "Reads", ["writes"] = "Writes", ["endpoint_calls"] = "Endpoint calls",
        ["errors"] = "Errors", ["bytes_in"] = "Bytes in", ["bytes_out"] = "Bytes out", ["compute_units"] = "Compute units",
        ["day"] = "Day", ["endpoint_slug"] = "Endpoint", ["calls"] = "Calls", ["duration_ms_sum"] = "Total duration (ms)",
    };

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
                tables.Add(Table("Player presence", await store.ReadProjectProfilesAsync(projectId, ct), NoTraffic, "steam_id", "player_name", "is_online", "last_seen_unix_ms", "total_seconds", "session_count", "last_event_type"));
                tables.Add(Table("Reported issues · " + date, await store.ListProjectIssuesAsync(projectId, date, 200, ct), "No issues were reported on this date.", "created_at_unix_ms", "steam_id", "category", "event_type", "label"));
                tables.Add(Table("Query executions", await store.ListQueryLastRunsAsync(projectId, ct), NoTraffic, "query_id", "run_at", "duration_ms", "keys_scanned", "records_returned", "from_cache"));
                break;
            case "logs":
                tables.Add(Table("Runtime requests · latest 200", await store.ListStorageRequestLogAsync(projectId, 200, ct),
                    $"No requests recorded in the last {RuntimeActivityLog.Retention.Days} days. Requests from the game, including rejected ones, appear here within a few seconds.",
                    "created_at_unix_ms", "method", "path", "status_code", "duration_ms"));
                tables.Add(Table("Owner audit trail · latest 200", await store.ListAuditLogsAsync(projectId, 200, ct), "No dashboard or CLI changes recorded yet.", "created_at_unix_ms", "action", "user_id", "log_id"));
                break;
            case "errors":
                tables.Add(Table("Errors · latest 200", await store.ListStorageErrorsAsync(projectId, 200, ct),
                    $"No errors recorded in the last {RuntimeActivityLog.Retention.Days} days.", "created_at_unix_ms", "severity", "error_id", "message", "source", "request_path"));
                break;
            case "usage":
                var monthly = await store.ReadProjectUsageMonthlyAsync(projectId, month, ct);
                tables.Add(Table("Monthly usage · " + month, monthly is { } row ? new[] { row } : [], "No usage recorded for this month.", "requests", "reads", "writes", "endpoint_calls", "errors", "bytes_in", "bytes_out", "compute_units"));
                tables.Add(Table("Daily usage", await store.ReadProjectUsageDailyAsync(projectId, month, ct), "No usage recorded for this month.", "day", "requests", "errors", "bytes_in", "bytes_out", "compute_units"));
                tables.Add(Table("Endpoint usage · up to 200", await store.ReadProjectUsageEndpointsAsync(projectId, month, 200, ct), "No endpoint calls recorded for this month.", "endpoint_slug", "calls", "errors", "duration_ms_sum", "compute_units"));
                break;
        }
        return View("~/Views/Owner/Activity.cshtml", new OwnerActivityModel(projectId, access.Project.Name, tab, month, date, tables));
    }

    private static OwnerActivityTable Table(string title, IReadOnlyList<JsonElement> rows, string empty, params string[] columns)
        => new(title, columns.Select(column => Labels.GetValueOrDefault(column, column)).ToArray(),
            rows.Select(row => new OwnerActivityRow(columns.Select(column => Display(row, column)).ToArray(), Help(row),
                row.TryGetProperty("stack_trace", out var stack) && stack.GetString() is { Length: > 0 } trace ? trace : null)).ToArray(),
            empty);

    private static string? Help(JsonElement row)
    {
        if (row.TryGetProperty("error_id", out var code)) return OwnerErrorHelp.ForCode(code.GetString());
        return row.TryGetProperty("status_code", out var status) && status.TryGetInt32(out var value) ? OwnerErrorHelp.ForStatus(value) : null;
    }

    private static string Display(JsonElement row, string column)
    {
        if (!row.TryGetProperty(column, out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (column.EndsWith("_unix_ms", StringComparison.Ordinal) && value.TryGetInt64(out var milliseconds))
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            _ => value.GetRawText(),
        };
    }
}
