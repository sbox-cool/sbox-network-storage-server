namespace SboxNetworkStorage.Contracts.Diagnostics;

public enum RouteOwner
{
    DotNetNative,
    DotNetShadow,
    BunCompatibility,
    PrivateWorker
}

public static class RouteOwnerNames
{
    public static string ToDisplayName(this RouteOwner owner) => owner switch
    {
        RouteOwner.DotNetNative => ".NET native",
        RouteOwner.DotNetShadow => ".NET shadow",
        RouteOwner.BunCompatibility => "Bun compatibility",
        RouteOwner.PrivateWorker => "private worker",
        _ => owner.ToString()
    };
}
