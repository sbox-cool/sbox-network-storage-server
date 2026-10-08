using SboxNetworkStorage.Contracts.Diagnostics;

namespace SboxNetworkStorage.Server.Routing;

public sealed record RouteOwnershipMetadata(RouteOwner Owner, string Description, bool LiveProductionResponseOwner = true);

public static class RouteOwnershipEndpointExtensions
{
    public static TBuilder WithRouteOwner<TBuilder>(this TBuilder builder, RouteOwner owner, string description, bool liveProductionResponseOwner = true)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RouteOwnershipMetadata(owner, description, liveProductionResponseOwner));
        return builder;
    }
}
