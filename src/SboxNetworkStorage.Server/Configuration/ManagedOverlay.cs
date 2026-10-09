using System.Text;

namespace SboxNetworkStorage.Server.Configuration;

/// <summary>Writes the overlay files that tunnel, DNS and telemetry commands own, into the runtime folder's <c>conf.d</c>.</summary>
public static class ManagedOverlay
{
    public static string PathFor(EffectiveConfig config, string file) => Path.Combine(config.OverlayDirectory, file);

    /// <summary>Replaces <paramref name="file"/> atomically with <paramref name="values"/> (full dotted keys).</summary>
    public static void Write(EffectiveConfig config, string file, string header, IReadOnlyDictionary<string, object> values)
    {
        var text = new StringBuilder($"# {header}\n");
        foreach (var group in values.GroupBy(pair => pair.Key[..pair.Key.LastIndexOf('.')]))
        {
            text.AppendLine($"[{group.Key}]");
            foreach (var pair in group)
            {
                text.AppendLine($"{pair.Key[(pair.Key.LastIndexOf('.') + 1)..]} = {ConfigFiles.FormatValue(pair.Value)}");
            }
        }

        using var identity = RuntimeIdentity.Enter(config);
        config.EnsureRuntimeDirectory();
        ConfigFiles.WriteAtomically(PathFor(config, file), text.ToString());
    }
}
