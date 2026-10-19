namespace SboxNetworkStorage.Server.Monitoring;

/// <summary>Hint text for Server page tiles whose availability depends on the operating system.</summary>
public static class MetricHints
{
    /// <summary>The hint under the load average tile, from whether this platform reports a load average at all.</summary>
    public static string LoadAverage(bool reportedOnThisPlatform, string osName) =>
        reportedOnThisPlatform ? "host-wide, 1-minute average" : $"not reported on {osName}";

    /// <summary>Linux (<c>/proc/loadavg</c>) and macOS (<c>getloadavg</c>) report a load average; Windows does not.</summary>
    public static bool LoadAverageReported() => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>The name of the operating system this process runs on, for hint text.</summary>
    public static string CurrentOsName() =>
        OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsLinux() ? "Linux"
        : "this platform";
}
