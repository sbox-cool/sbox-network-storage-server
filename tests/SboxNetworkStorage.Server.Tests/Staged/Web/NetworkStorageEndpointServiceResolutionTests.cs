using SboxNetworkStorage.Server.Tests.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Guards against the DI-registration class of bug that took down every
/// PUT/POST/DELETE /v3/manage/* mutation (correlation req_a9795ba7…): the
/// gateway resolves handlers by CONCRETE type via
/// <c>GetRequiredService&lt;TConcrete&gt;()</c> at request time, but the handler
/// was registered only under an interface, so DI threw
/// "No service for type ... has been registered" and surfaced as a 500 on every
/// matching request.
///
/// ValidateOnBuild would NOT have caught this (it validates registered service
/// graphs, not runtime concrete resolutions), and this repo cannot enable
/// ValidateOnBuild anyway because it eagerly instantiates singletons at startup,
/// which conflicts with the "unavailable Postgres/Scylla must never block host
/// startup" invariant. So this test codifies the contract explicitly: every
/// concrete type a Network Storage endpoint resolves from RequestServices MUST
/// be resolvable from a request scope.
///
/// When you add an endpoint that resolves a concrete type by
/// <c>GetRequiredService&lt;TConcrete&gt;()</c>, add that type here.
/// </summary>
public abstract class NetworkStorageEndpointServiceResolutionTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly SelfHostFactory factory;

    protected NetworkStorageEndpointServiceResolutionTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    public static TheoryData<Type> ConcreteTypesEndpointsResolve() => new()
    {
        // NetworkStorageGatewayEndpoints
        typeof(ManagementMutationCandidateHandler), // ServeNativeManagementMutationAsync (the incident)
        typeof(EndpointSlugReadCandidateHandler),   // ServeNativeEndpointSlugReadAsync
        typeof(PackageSyncHandler),                 // ServeNativePackageSyncAsync
        typeof(NativeStatsHeartbeatHandler),        // ServeNativeStatsHeartbeatAsync
        // EndpointExecutionEndpoints
        typeof(NativeEndpointShadowExecutor),
        // QueryEndpoints
        typeof(NativeQueryExecutor),
    };

    [SkippableTheory]
    [MemberData(nameof(ConcreteTypesEndpointsResolve))]
    public void ConcreteTypeResolvesFromRequestScope(Type serviceType)
    {
        using var scope = factory.Services.CreateScope();

        var resolved = scope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(resolved);
    }
}

public sealed class NetworkStorageEndpointServiceResolutionTests_Sqlite(SqliteHostFactory factory) : NetworkStorageEndpointServiceResolutionTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageEndpointServiceResolutionTests_Postgres(PostgresHostFactory factory) : NetworkStorageEndpointServiceResolutionTests<PostgresHostFactory>(factory);
