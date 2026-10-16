namespace SboxNetworkStorage.EndpointSpike.Sandbox;

/// <summary>
/// Explicit worker launch. Production uses the current process host and entry assembly; tests run inside a test
/// host, so they name the spike worker explicitly (e.g. <c>dotnet</c> plus the spike assembly path).
/// <see cref="PrefixArguments"/> precede <c>--worker</c>.
/// </summary>
public sealed record WorkerLaunch(string FileName, IReadOnlyList<string> PrefixArguments);
