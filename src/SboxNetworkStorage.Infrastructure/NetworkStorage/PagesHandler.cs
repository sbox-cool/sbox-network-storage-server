using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Read-only native handler for <c>GET /api/pages/:projectId/:pageSlug</c> and
/// <c>GET /pages/:projectId/:pageSlug</c>. Mirrors legacy server's <c>routePagesApiGet</c>: resolves the
/// project owner from the <c>network-storage/page-index/{projectId}.json</c> index, then reads
/// the published page JSON from <c>network-storage/users/{userId}/{projectId}/pages/{pageSlug}.json</c>.
/// Supports <c>format</c> query parameter (<c>"json"</c> / <c>"jsonmd"</c>); legacy server's HTML and markdown
/// format responses are out of scope for this read-only handler. Reads only; never writes.
/// </summary>
public sealed class PagesHandler : INetworkStorageHandler
{
    private readonly IWorkspaceStore _workspaceClient;

    public PagesHandler(IWorkspaceStore workspaceClient)
    {
        _workspaceClient = workspaceClient;
    }

    public NetworkStorageRouteFamily Family => NetworkStorageRouteFamily.Pages;

    public bool CanHandle(NetworkStorageRouteClassification route) =>
        route.Family == NetworkStorageRouteFamily.Pages
        && string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase);

    public async Task<NetworkStorageResult> ExecuteAsync(NetworkStorageRequest request)
    {
        var projectId = request.RouteParameter("projectId") ?? string.Empty;
        var pageSlug = request.RouteParameter("pageSlug") ?? string.Empty;
        var format = (request.QueryValue("format") ?? "json").ToLowerInvariant();

        // ── Step 1: read page-index to resolve project owner ──

        var pageIndexPath = $"network-storage/page-index/{Uri.EscapeDataString(projectId)}.json";
        var storagePathsRead = new List<string>();

        JsonElement? pageIndex;
        try
        {
            pageIndex = await _workspaceClient.GetRawAsync<JsonElement?>(pageIndexPath, request.CancellationToken);
            storagePathsRead.Add(pageIndexPath);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return NetworkStorageResult.Error(
                404,
                "PAGE_READ_FAILED",
                new
                {
                    ok = false,
                    error = new { code = "PAGE_READ_FAILED", message = "Page could not be read. It may be corrupt or inaccessible." },
                    projectId,
                    source = "error"
                },
                storagePathsRead,
                authDecision: "anonymous");
        }

        if (pageIndex is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return NetworkStorageResult.Error(
                404,
                "NOT_FOUND",
                new { error = new { code = "NOT_FOUND", message = "Project not found." } },
                storagePathsRead,
                authDecision: "anonymous");
        }

        // legacy server interpolates `ownerData.userId` directly into the storage path regardless of JSON
        // type. Production page-index documents store `userId` as a String (older writers) or a
        // Number; both must resolve to the same path segment. Calling GetInt64() unconditionally
        // crashed on string-typed ids (ENDPOINT/PAGES read 500s on GET /pages/{projectId}/updates).
        if (!pageIndex.Value.TryGetProperty("userId", out var userIdElement)
            || userIdElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return NetworkStorageResult.Error(
                404,
                "NOT_FOUND",
                new { error = new { code = "NOT_FOUND", message = "Project not found." } },
                storagePathsRead,
                authDecision: "anonymous");
        }

        var userId = userIdElement.ValueKind switch
        {
            JsonValueKind.String => userIdElement.GetString() ?? string.Empty,
            JsonValueKind.Number => userIdElement.GetRawText(),
            _ => userIdElement.GetRawText(),
        };

        // ── Step 2: read the published page content ──

        var pagePath = $"network-storage/users/{userId}/{Uri.EscapeDataString(projectId)}/pages/{Uri.EscapeDataString(pageSlug)}.json";

        JsonElement? page;
        try
        {
            page = await _workspaceClient.GetRawAsync<JsonElement?>(pagePath, request.CancellationToken);
            storagePathsRead.Add(pagePath);
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            return NetworkStorageResult.Error(
                404,
                "PAGE_READ_FAILED",
                new
                {
                    ok = false,
                    error = new { code = "PAGE_READ_FAILED", message = "Page could not be read. It may be corrupt or inaccessible." },
                    projectId,
                    source = "error"
                },
                storagePathsRead,
                authDecision: "anonymous");
        }

        if (page is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return NetworkStorageResult.Error(
                404,
                "NOT_FOUND",
                new { error = new { code = "NOT_FOUND", message = "Page not found." } },
                storagePathsRead,
                authDecision: "anonymous");
        }

        // ── Step 3: shape legacy-compatible JSON response ──

        var pageType = page.Value.TryGetProperty("pageType", out var typeEl) ? typeEl.GetString() : null;
        var statusLevel = page.Value.TryGetProperty("statusLevel", out var statusEl) ? statusEl.GetString() : null;

        var payload = new Dictionary<string, object?>
        {
            ["slug"] = page.Value.TryGetProperty("slug", out var slugEl) ? slugEl.GetString() : null,
            ["title"] = page.Value.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null,
            ["type"] = pageType,
            ["statusLevel"] = statusLevel,
            ["updatedAt"] = page.Value.TryGetProperty("updatedAt", out var updatedEl) ? updatedEl.GetString() : null,
        };

        if (string.Equals(pageType, "keyvalue", StringComparison.OrdinalIgnoreCase))
        {
            payload["data"] = page.Value.TryGetProperty("kvData", out var kvData)
                ? kvData
                : new Dictionary<string, object>();
        }
        else
        {
            payload["markdown"] = page.Value.TryGetProperty("contentMd", out var contentMd)
                ? contentMd.GetString()
                : null;
        }

        // Include html field only for format=json parity (handler cannot render HTML)
        if (format == "json")
        {
            payload["html"] = "";
        }

        return NetworkStorageResult.Ok(payload, storagePathsRead, authDecision: "anonymous");
    }
}
