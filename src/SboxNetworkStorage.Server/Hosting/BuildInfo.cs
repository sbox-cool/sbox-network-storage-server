using System.Reflection;

namespace SboxNetworkStorage.Server.Hosting;

public static class BuildInfo
{
    /// <summary>Semantic version of this build (set by <c>-p:Version=</c> in the release workflow).</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>Runtime identifier of this build, e.g. <c>linux-x64</c>.</summary>
    public static string RuntimeIdentifier => System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

    private static string ResolveVersion()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
