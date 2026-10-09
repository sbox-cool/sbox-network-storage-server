using System.Security.Cryptography;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// C# port of the legacy server random step's value generation (int, float, weighted, beta).
/// Uses <see cref="RandomNumberGenerator"/> for crypto-secure randomness.
/// </summary>
public static class RandomValueGenerator
{
    /// <summary>Uniform random integer in [min, max) — matches legacy server's <c>randomInt(min, max)</c>.</summary>
    public static double RandomInt(double min, double max)
    {
        var lo = (long)Math.Floor(min);
        var hi = (long)Math.Floor(max);
        if (lo >= hi) return lo;
        return lo + (long)(RandomNumberGenerator.GetInt32(int.MaxValue) * (double)(hi - lo) / int.MaxValue);
    }

    /// <summary>Uniform random float in [min, max] — matches legacy server's <c>randomFloat(min, max)</c>.</summary>
    public static double RandomFloat(double min, double max)
    {
        var unit = RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue;
        return min + unit * (max - min);
    }

    /// <summary>
    /// Weighted random selection from items. Each item is a dictionary with a
    /// configurable weight field (default "weight"). Returns the selected item.
    /// Matches legacy server's <c>weightedSelect(items, weightField)</c>.
    /// </summary>
    public static object? WeightedSelect(List<object?> items, string weightField = "weight")
    {
        if (items.Count == 0) return null;
        if (items.Count == 1) return items[0];

        var totalWeight = 0d;
        foreach (var item in items)
        {
            var w = GetWeight(item, weightField);
            totalWeight += w > 0 ? w : 0;
        }
        if (totalWeight <= 0) return items[RandomNumberGenerator.GetInt32(items.Count)];

        var roll = RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue * totalWeight;
        var cumulative = 0d;
        foreach (var item in items)
        {
            var w = GetWeight(item, weightField);
            if (w <= 0) continue;
            cumulative += w;
            if (roll < cumulative) return item;
        }
        return items[^1]; // floating-point edge case
    }

    private static double GetWeight(object? item, string weightField)
    {
        if (item is Dictionary<string, object?> dict && dict.TryGetValue(weightField, out var w))
            return EndpointExpression.JsNumber(w);
        return 1d;
    }

    /// <summary>
    /// Beta-distributed float (alpha=1 → right-skewed, alpha=beta → bell-shaped).
    /// Matches the legacy server approximation: <c>betaOneN</c> and <c>betaSymmetric</c>.
    /// </summary>
    public static double BetaSample(double alpha, double beta, double min, double max)
    {
        double val;
        if (Math.Abs(alpha - 1) < 1e-9)
            val = BetaOneN(beta, min, max);
        else if (Math.Abs(alpha - beta) < 1e-9)
            val = BetaSymmetric(alpha, min, max);
        else
            val = BetaSymmetric(Math.Ceiling((alpha + beta) / 2), min, max);
        return val;
    }

    // alpha=1 approximation: power-law from max
    private static double BetaOneN(double beta, double min, double max)
    {
        var u = RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue;
        var val = max - (max - min) * Math.Pow(u, 1.0 / beta);
        return val;
    }

    // Symmetric beta approximation: average of alpha uniform samples
    private static double BetaSymmetric(double alpha, double min, double max)
    {
        var sum = 0d;
        var n = (int)Math.Max(1, Math.Round(alpha));
        for (var i = 0; i < n; i++)
            sum += RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue;
        var val = min + (sum / n) * (max - min);
        return val;
    }
}
