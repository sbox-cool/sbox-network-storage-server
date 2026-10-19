using System.Reflection;

namespace SboxNetworkStorage.Server.Hosting;

public static class BuildInfo
{
    /// <summary>Semantic version of this build (set by <c>-p:Version=</c> in the release workflow).</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>Runtime identifier of this build, e.g. <c>linux-x64</c>.</summary>
    public static string RuntimeIdentifier => System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

    /// <summary>True when this build is a development build (version carries a <c>dev</c> pre-release identifier).</summary>
    public static bool IsDevelopmentBuild => IsDevelopmentVersion(Version);

    /// <summary>
    /// True when <paramref name="version"/> carries a <c>dev</c> pre-release identifier, such as <c>0.1.0-dev</c>.
    /// Development builds are never offered a release as an update: they are not part of the release line.
    /// </summary>
    public static bool IsDevelopmentVersion(string version)
    {
        var dash = version.IndexOf('-');
        if (dash < 0) return false;
        var end = version.IndexOf('+', dash);
        var prerelease = end < 0 ? version[(dash + 1)..] : version[(dash + 1)..end];
        return prerelease.Split('.').Any(identifier => string.Equals(identifier, "dev", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveVersion()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
