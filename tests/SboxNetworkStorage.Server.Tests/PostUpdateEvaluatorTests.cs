using SboxNetworkStorage.Server.Updates;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Pure verdict rules: per-library error rate against the previous binary, with traffic floors.</summary>
public sealed class PostUpdateEvaluatorTests
{
    private const string Current = "2.0.0";
    private const string Previous = "1.9.0";
    private static readonly VerdictThresholds Thresholds = VerdictThresholds.Default;

    private static ClientOutcomeTotals Row(string server, string library, long requests, long failures, long lastHour = 100) =>
        new(server, library, requests, failures, 0, lastHour);

    private static PostUpdateVerdict Evaluate(IEnumerable<ClientOutcomeTotals> current, IEnumerable<ClientOutcomeTotals> baseline) =>
        PostUpdateEvaluator.Evaluate(Current, 24, current, baseline, Thresholds);

    [Fact]
    public void NoPreviousBinaryTrafficIsNoBaseline()
    {
        var verdict = Evaluate([Row(Current, "1.1.0", 500, 400)], []);

        Assert.Equal(VerdictStatus.NoBaseline, verdict.Status);
        Assert.Null(verdict.BaselineVersion);
        Assert.Equal(LibraryOutcome.NoBaseline, Assert.Single(verdict.Libraries).Outcome);
    }

    [Fact]
    public void ErrorRateJumpPastBothThresholdsIsRegression()
    {
        var verdict = Evaluate(
            [Row(Current, "1.1.0", 100, 30)],
            [Row(Previous, "1.1.0", 100, 5)]);

        Assert.Equal(VerdictStatus.Regression, verdict.Status);
        Assert.Equal(Previous, verdict.BaselineVersion);
        var library = Assert.Single(verdict.Libraries);
        Assert.Equal(LibraryOutcome.Regressed, library.Outcome);
        Assert.Equal(0.30m, library.PostErrorRate);
        Assert.Equal(0.05m, library.BaselineErrorRate);
    }

    [Fact]
    public void ExactlyAtBothThresholdsRegresses()
    {
        // Baseline 10% -> 20%: the point delta is exactly 0.10 and the ratio exactly 2.0.
        var verdict = Evaluate(
            [Row(Current, "1.1.0", 50, 10)],
            [Row(Previous, "1.1.0", 50, 5)]);

        Assert.Equal(VerdictStatus.Regression, verdict.Status);
    }

    [Fact]
    public void RatioBelowThresholdIsNotRegression()
    {
        // 40% -> 50%: point delta is 0.10 but the ratio is only 1.25.
        var verdict = Evaluate(
            [Row(Current, "1.1.0", 100, 50)],
            [Row(Previous, "1.1.0", 100, 40)]);

        Assert.Equal(VerdictStatus.Ok, verdict.Status);
        Assert.Equal(LibraryOutcome.Ok, Assert.Single(verdict.Libraries).Outcome);
    }

    [Fact]
    public void LowCurrentTrafficIsInsufficientDataNotRegression()
    {
        var verdict = Evaluate(
            [Row(Current, "1.1.0", 49, 40)],
            [Row(Previous, "1.1.0", 500, 5)]);

        Assert.Equal(VerdictStatus.InsufficientData, verdict.Status);
        Assert.Equal(LibraryOutcome.LowTraffic, Assert.Single(verdict.Libraries).Outcome);
    }

    [Fact]
    public void LowBaselineTrafficIsNotJudged()
    {
        var verdict = Evaluate(
            [Row(Current, "1.1.0", 500, 400)],
            [Row(Previous, "1.1.0", 49, 0)]);

        Assert.Equal(VerdictStatus.InsufficientData, verdict.Status);
        Assert.Equal(LibraryOutcome.LowTraffic, Assert.Single(verdict.Libraries).Outcome);
    }

    [Fact]
    public void LibraryAbsentFromPreviousBinaryIsNewAndNeverJudged()
    {
        var verdict = Evaluate(
            [Row(Current, "1.2.0", 500, 400), Row(Current, "1.1.0", 100, 5)],
            [Row(Previous, "1.1.0", 100, 5)]);

        var fresh = verdict.Libraries.Single(library => library.Library == "1.2.0");
        Assert.Equal(LibraryOutcome.NewLibrary, fresh.Outcome);
        Assert.Equal(VerdictStatus.Ok, verdict.Status);
    }

    [Fact]
    public void RegressionInOneLibraryFailsTheWholeVerdict()
    {
        var verdict = Evaluate(
            [Row(Current, "1.1.0", 100, 5), Row(Current, "1.0.0", 100, 60)],
            [Row(Previous, "1.1.0", 100, 5), Row(Previous, "1.0.0", 100, 5)]);

        Assert.Equal(VerdictStatus.Regression, verdict.Status);
        Assert.Equal(["1.0.0", "1.1.0"], verdict.Libraries.Select(library => library.Library));
        Assert.Equal(LibraryOutcome.Regressed, verdict.Libraries.Single(library => library.Library == "1.0.0").Outcome);
    }

    [Fact]
    public void BaselineIsTheMostRecentOtherBinaryNotTheCurrentOne()
    {
        var verdict = Evaluate(
            [Row(Current, "1.1.0", 100, 5)],
            [
                Row(Current, "1.1.0", 100, 60, lastHour: 500),
                Row("1.8.0", "1.1.0", 100, 5, lastHour: 200),
                Row(Previous, "1.1.0", 100, 5, lastHour: 300),
            ]);

        Assert.Equal(Previous, verdict.BaselineVersion);
        Assert.Equal(VerdictStatus.Ok, verdict.Status);
    }

    [Fact]
    public void NoLibrariesInWindowIsInsufficientDataWhenBaselineExists()
    {
        var verdict = Evaluate([], [Row(Previous, "1.1.0", 100, 5)]);

        Assert.Equal(VerdictStatus.InsufficientData, verdict.Status);
        Assert.Empty(verdict.Libraries);
    }
}
