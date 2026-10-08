using System.Collections.Concurrent;

namespace SboxNetworkStorage.Server.Middleware;

public static class DependencyTimingContext
{
    private const string ItemKey = "sboxcool.dependency_timings";

    public static void Record(HttpContext context, string dependency, double elapsedMilliseconds)
    {
        var timings = GetOrCreate(context);
        timings.AddOrUpdate(dependency, elapsedMilliseconds, (_, existing) => existing + elapsedMilliseconds);
    }

    public static IReadOnlyDictionary<string, double> Snapshot(HttpContext context)
    {
        if (!context.Items.TryGetValue(ItemKey, out var value) || value is not ConcurrentDictionary<string, double> timings)
        {
            return new Dictionary<string, double>();
        }

        return timings.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static ConcurrentDictionary<string, double> GetOrCreate(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var value) && value is ConcurrentDictionary<string, double> existing)
        {
            return existing;
        }

        var created = new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        context.Items[ItemKey] = created;
        return created;
    }
}
