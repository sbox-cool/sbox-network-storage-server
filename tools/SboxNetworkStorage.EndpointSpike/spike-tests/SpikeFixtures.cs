using System.Text;
using SboxNetworkStorage.EndpointSpike.Compiler;

namespace SboxNetworkStorage.EndpointSpike.Tests;

/// <summary>Locates spike-root files relative to the test binary, without copying them into test output.</summary>
internal static class SpikeFixtures
{
    private static readonly Lazy<string> Root = new(FindSpikeRoot);

    public static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(Root.Value, "fixtures", name), Encoding.UTF8);

    public static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"sbox-ns-spike-tests-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Pads <paramref name="source"/> with a trailing line comment so its UTF-8 size is exactly
    /// <paramref name="totalBytes"/>. The source must be ASCII.
    /// </summary>
    public static string PadToBytes(string source, int totalBytes)
    {
        var fixedBytes = Encoding.UTF8.GetByteCount(source) + 3; // "//" + trailing "\n"
        if (totalBytes < fixedBytes)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        return source + "//" + new string('x', totalBytes - fixedBytes) + "\n";
    }

    private static string FindSpikeRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SboxNetworkStorage.EndpointSpike.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("SboxNetworkStorage.EndpointSpike.csproj not found above the test binary.");
    }
}
