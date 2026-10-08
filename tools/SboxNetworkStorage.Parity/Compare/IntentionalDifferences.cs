using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Parity.Compare;

/// <summary>
/// Reviewed differences that <c>parity diff</c> tolerates. Every entry needs a
/// reason; <c>scenario</c>, <c>step</c> and <c>path</c> accept <c>*</c> globs.
/// </summary>
public sealed class IntentionalDifferences
{
    public int SchemaVersion { get; set; } = 1;

    public List<Entry> Differences { get; set; } = [];

    public static IntentionalDifferences Empty { get; } = new();

    public static IntentionalDifferences Load(string? path)
    {
        if (path is null)
        {
            return Empty;
        }

        if (!File.Exists(path))
        {
            throw new ParityException($"allow-list not found: {path}");
        }

        var loaded = JsonSerializer.Deserialize<IntentionalDifferences>(File.ReadAllText(path), Json.Options) ?? Empty;
        foreach (var entry in loaded.Differences)
        {
            if (string.IsNullOrWhiteSpace(entry.Reason))
            {
                throw new ParityException($"{path}: every intentional difference needs a reason ({entry.Scenario}/{entry.Step}/{entry.Path})");
            }
        }

        return loaded;
    }

    public string? Match(string scenario, string step, string path) =>
        Differences.FirstOrDefault(e => Glob(e.Scenario, scenario) && Glob(e.Step, step) && Glob(e.Path, path))?.Reason;

    private static bool Glob(string pattern, string value) =>
        Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);

    public sealed class Entry
    {
        public string Scenario { get; set; } = "*";

        public string Step { get; set; } = "*";

        public string Path { get; set; } = "*";

        public string Reason { get; set; } = "";
    }
}
