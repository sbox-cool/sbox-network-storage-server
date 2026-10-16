using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SboxNetworkStorage.EndpointSpike.Plan;

/// <summary>
/// Deterministic JSON: object keys sorted ordinally, doubles in round-trip invariant form, no whitespace.
/// Used for plan hashing so the same source always yields the same bytes.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    public static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d))
                    throw new ArgumentException("Non-finite number cannot be canonicalized.");
                sb.Append(d == 0 ? "0" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                return;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                return;
            case string s:
                sb.Append(JsonSerializer.Serialize(s));
                return;
            case IDictionary<string, object?> map:
            {
                sb.Append('{');
                var first = true;
                foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(key));
                    sb.Append(':');
                    Write(sb, map[key]);
                }
                sb.Append('}');
                return;
            }
            case System.Collections.IEnumerable list:
            {
                sb.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }
            default:
                throw new ArgumentException($"Unsupported canonical JSON value type {value.GetType().Name}.");
        }
    }
}
