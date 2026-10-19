using SboxNetworkStorage.Server.Monitoring;

namespace SboxNetworkStorage.Server.Tests.Monitoring;

/// <summary>
/// Regression: the load average tile said "not reported on Windows" on every platform, including macOS where
/// a value is read. The hint must follow whether the platform reports load at all, not whether one sample happened to exist.
/// </summary>
public sealed class MetricHintsTests
{
    [Theory]
    [InlineData("macOS")]
    [InlineData("Linux")]
    public void A_platform_that_reports_load_never_claims_a_gap(string os)
    {
        var hint = MetricHints.LoadAverage(reportedOnThisPlatform: true, os);

        Assert.Equal("host-wide, 1-minute average", hint);
        Assert.DoesNotContain("not reported", hint);
    }

    [Fact]
    public void A_platform_without_load_names_the_actual_operating_system()
        => Assert.Equal("not reported on Windows", MetricHints.LoadAverage(reportedOnThisPlatform: false, "Windows"));

    [Fact]
    public void The_load_hint_follows_the_current_platform()
    {
        var reported = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

        Assert.Equal(reported, MetricHints.LoadAverageReported());
    }

    [Fact]
    public void The_current_operating_system_is_named_for_the_hint()
    {
        var name = MetricHints.CurrentOsName();

        Assert.Equal(OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "this platform", name);
    }
}
