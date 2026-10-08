using SboxNetworkStorage.Contracts.Diagnostics;

namespace SboxNetworkStorage.Application.Diagnostics;

public interface IRouteOwnershipRegistry
{
    IReadOnlyList<RouteOwnershipRecord> ListOwners();

    void Add(RouteOwnershipRecord record);
}

public sealed class RouteOwnershipRegistry : IRouteOwnershipRegistry
{
    private readonly List<RouteOwnershipRecord> records = [];
    private readonly Lock gate = new();

    public IReadOnlyList<RouteOwnershipRecord> ListOwners()
    {
        lock (gate)
        {
            return records.ToArray();
        }
    }

    public void Add(RouteOwnershipRecord record)
    {
        lock (gate)
        {
            if (records.Any(existing => string.Equals(existing.Pattern, record.Pattern, StringComparison.OrdinalIgnoreCase))) return;
            records.Add(record);
        }
    }
}
