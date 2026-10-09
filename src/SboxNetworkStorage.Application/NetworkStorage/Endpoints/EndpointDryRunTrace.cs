namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>One executed top-level step: its id, type, the value it bound in the context, and whether it ended the run with an error.</summary>
public sealed record EndpointStepTrace(string Id, string Type, object? Result, bool Passed);

/// <summary>A record write or delete the endpoint queued. In a dry run it is captured here and never persisted.</summary>
public sealed record EndpointTraceWrite(string Collection, string Key, IReadOnlyDictionary<string, object?>? Data, bool IsDelete);

/// <summary>
/// Opt-in observer for test runs: collects the executed step trace and the writes the
/// endpoint would perform. Passing one to <see cref="NativeEndpointShadowExecutor.TryExecuteAsync"/>
/// does not change execution semantics.
/// </summary>
public sealed class EndpointDryRunTrace
{
    public List<EndpointStepTrace> Steps { get; } = new();
    public List<EndpointTraceWrite> PendingWrites { get; } = new();
}
