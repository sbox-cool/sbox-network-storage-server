using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Middleware;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// Opt-in library enforcement. A request is refused with HTTP 426 only when (a) the project's owner set an
/// explicit library minimum and the client reports a parsed version below it, or (b) the client reports a
/// parsed version in a <c>blocked</c> range of the embedded matrix (<see cref="LibraryCompatProvider.Embedded"/>).
/// The release-feed overlay never refuses anything: it stays report-only in the status headers.
/// Requests with no, legacy or unparseable library version are never refused here, and never pay for
/// key resolution or the project lookup. The minimum lookup fails open: a lookup error is logged and
/// treated as "no minimum". Version metadata is self-reported, never authentication: missing or
/// unauthorized keys keep their original 401/403 contract, and management diagnostics and recovery
/// (Pull, Test Live, revision-init) stay reachable.
/// </summary>
public sealed class LibraryPolicyMiddleware(RequestDelegate next, LibraryCompatProvider compat, LibraryPolicyCache policies, ILogger<LibraryPolicyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IStorageApiKeyResolver keys, INetworkStorageProjectService projects)
    {
        if (HttpMethods.IsOptions(context.Request.Method) || context.GetRouteValue("projectId") is not string projectId)
        {
            await next(context);
            return;
        }
        var path = NetworkStorageRequestRoot.OriginalPath(context);
        var management = NetworkStorageRoots.IsManagementPath(path);
        var push = management && (HttpMethods.IsPut(context.Request.Method) && path.EndsWith("/sync", StringComparison.Ordinal)
            || HttpMethods.IsPost(context.Request.Method) && (path.EndsWith("/sync/preflight", StringComparison.Ordinal)
                || path.EndsWith("/package-sync", StringComparison.Ordinal)));
        // Keep management diagnostics/recovery (Pull, Test Live, revision-init) available even when runtime policy blocks.
        if (management && !push) { await next(context); return; }
        var identity = ClientIdentityContext.Get(context);
        // Legacy and unknown clients predate enforcement and can never be refused: skip key
        // resolution and the project lookup entirely. This mirrors the client-side parse in
        // ClientVersionsQuery.BelowMinimum so the two cannot disagree about what is parseable.
        if (!ClientIdentity.TryParseLibraryVersion(identity.LibraryVersion, out _))
        {
            await next(context);
            return;
        }
        var verdict = compat.Embedded.Evaluate(identity);
        var block = verdict.Status is LibraryStatus.Blocked ? verdict : null;
        string? minimum;
        try
        {
            minimum = await policies.MinimumAsync(projects, projectId, context.RequestAborted);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            // The minimum is advisory data for an opt-in rule: never turn a lookup failure into a 500.
            // Nothing is cached, so recovery is immediate on the next request.
            logger.LogWarning(ex, "Library minimum lookup failed for project {ProjectId}; failing open.", projectId);
            minimum = null;
        }
        var unsupported = ClientVersionsQuery.BelowMinimum(identity.LibraryVersion, minimum);
        if (block is null && !unsupported) { await next(context); return; }
        var apiKey = context.Request.Headers["x-api-key"].FirstOrDefault()
            ?? context.Request.Query["apiKey"].FirstOrDefault() ?? context.Request.Headers["x-public-key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey) || await keys.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted) is not { Enabled: true } auth)
        {
            await next(context); // Existing route keeps its own auth/error contract.
            return;
        }
        if (push && (string.IsNullOrWhiteSpace(context.Request.Headers["x-api-key"])
            || !string.Equals(auth.KeyType, "secret", StringComparison.OrdinalIgnoreCase) || !PackageSyncHandler.HasWritePermissions(auth)))
        {
            await next(context); // Do not replace the original management 401/403 with a version rejection.
            return;
        }
        NetworkStorageUsageContext.SetAuthenticated(context, projectId);
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        var code = block is null ? "LIBRARY_UNSUPPORTED" : "LIBRARY_BLOCKED";
        context.Items[typeof(LibraryPolicyMiddleware)] = block is null ? "unsupported" : "blocked";
        var message = block is null
            ? $"This project's owner requires library {minimum ?? compat.Embedded.Minimum} or newer. Update the library and republish the game."
            : string.Join(" ", new[] { block.Reason, block.Advisory is null ? null : $"Advisory: {block.Advisory}.",
                "Update the library and republish the game." }.Where(part => !string.IsNullOrWhiteSpace(part)));
        await context.Response.WriteAsJsonAsync(new { ok = false, error = new { code, message } }, context.RequestAborted);
    }
}
