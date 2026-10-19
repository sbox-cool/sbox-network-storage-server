namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Tests that build signed configs or write the signing process environment
/// (NETWORK_STORAGE_SECURITY_CONFIG_*) share this collection, so they never overlap.
/// Test classes outside it can still run in parallel, so they must not assert the key id.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SecurityConfigProcessEnvironmentCollection
{
    public const string Name = "SecurityConfigProcessEnvironment";
}
