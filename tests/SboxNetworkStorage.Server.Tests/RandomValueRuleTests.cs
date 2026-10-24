using SboxNetworkStorage.Application.NetworkStorage.Endpoints;

namespace SboxNetworkStorage.Server.Tests;

public sealed class RandomValueRuleTests
{
    [Theory]
    [InlineData(5, 2)]
    [InlineData(5, 5)]
    public void Reversed_or_empty_float_range_returns_the_lower_argument(double min, double max)
        => Assert.Equal(min, RandomValueGenerator.RandomFloat(min, max));

    [Fact]
    public void Missing_and_nonnumeric_weights_are_zero_not_implicit_loot_entries()
    {
        Assert.Equal(0, RandomValueGenerator.GetWeight(null, "weight"));
        Assert.Equal(0, RandomValueGenerator.GetWeight(new Dictionary<string, object?>(), "weight"));
        Assert.Equal(0, RandomValueGenerator.GetWeight(new Dictionary<string, object?> { ["weight"] = "invalid" }, "weight"));
        Assert.Equal(2.5, RandomValueGenerator.GetWeight(new Dictionary<string, object?> { ["weight"] = "2.5" }, "weight"));
    }

    [Theory]
    [InlineData(-1, 7.5)]
    [InlineData(0, 7.5)]
    [InlineData(0.5, 7.5)]
    [InlineData(1, 7.5)]
    [InlineData(2, 5)]
    public void Beta_one_n_clamps_the_shape_but_preserves_shapes_above_one(double shape, double expected)
        => Assert.Equal(expected, RandomValueGenerator.BetaOneN(shape, 0, 10, 0.25), precision: 10);
}
