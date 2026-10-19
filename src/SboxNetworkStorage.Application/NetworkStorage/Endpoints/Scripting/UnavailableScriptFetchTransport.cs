namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>The transport used when no network transport is registered. Every call fails closed with FETCH_FAILED.</summary>
public sealed class UnavailableScriptFetchTransport : IScriptFetchTransport
{
    public bool AllowPrivateNetworks => false;

    public Task<ScriptFetchResult> SendAsync(ScriptFetchRequest request, EndpointFetchPolicy policy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var host = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        return Task.FromResult(ScriptFetchResult.Fail(new ScriptFetchFailure("FETCH_FAILED", "unavailable", host)));
    }
}
