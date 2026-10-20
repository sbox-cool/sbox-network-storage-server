using System.Text.RegularExpressions;
using SboxNetworkStorage.Server.Tests.Compat;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// docs/ui-guidelines.md: a rounded element is outlined on every side or not at all. A single-edge accent (a left
/// rail, an inset side shadow, an underline) is allowed only on a flat element, and the rule must say so with
/// <c>border-radius: 0</c>, because a rule that stays silent can be applied to a rounded base class declared elsewhere.
/// </summary>
public sealed partial class OwnerCssShapeGuardTests
{
    [Fact]
    public void Owner_stylesheets_have_no_single_edge_accent_on_rounded_elements()
    {
        var directory = Path.Combine(CompatTestPaths.RepoRoot, "src", "SboxNetworkStorage.Server", "wwwroot", "css");
        var files = Directory.GetFiles(directory, "*.css");
        Assert.NotEmpty(files);

        var violations = files.SelectMany(file => Violations(File.ReadAllText(file), Path.GetFileName(file))).ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData(".notice { border: 1px solid #000; border-left: 3px solid red; border-radius: 6px; }", true)]
    [InlineData(".toast { border-radius: 6px; box-shadow: inset 2px 0 0 red; }", true)]
    [InlineData(".toast[data-kind=\"ok\"] { box-shadow: var(--shadow), inset 2px 0 0 red; }", true)]
    [InlineData(".tab { border-radius: 6px 6px 0 0; box-shadow: inset 0 -2px red; }", true)]
    [InlineData(".tab { border-radius: var(--radius-sm); border-bottom: 2px solid red; }", true)]
    [InlineData(".rail { border-left: 3px solid red; }", true)]
    [InlineData(".tab { border-radius: 0; box-shadow: inset 0 -2px red; }", false)]
    [InlineData(".rail { border-radius: 0; border-left: 3px solid red; }", false)]
    [InlineData(".row { border-bottom: 1px solid #000; }", false)]
    [InlineData(".toast { border: 1px solid red; border-radius: 6px; box-shadow: 0 8px 24px rgb(0 0 0 / .3); }", false)]
    [InlineData("@media (max-width: 760px) { .card { border-radius: 8px; border-top: 4px solid red; } }", true)]
    public void Detector_flags_exactly_the_single_edge_accent_patterns(string css, bool flagged) =>
        Assert.Equal(flagged, Violations(css, "sample.css").Any());

    private static IEnumerable<string> Violations(string css, string file)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        foreach (Match rule in RuleBlock().Matches(css))
        {
            var selector = rule.Groups["selector"].Value.Trim();
            var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var text in rule.Groups["body"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = text.Split(':', 2);
                if (parts.Length == 2) declarations[parts[0].Trim().ToLowerInvariant()] = parts[1].Trim();
            }

            var flat = declarations.TryGetValue("border-radius", out var radius) && IsZero(radius);
            var rounded = declarations.ContainsKey("border-radius") && !flat;

            foreach (var (property, value) in declarations)
            {
                if (SingleSideBorder().IsMatch(property) && !IsZero(value) && !value.StartsWith("none", StringComparison.Ordinal)
                    && (rounded || (IsRail(value) && !flat)))
                    yield return $"{file}: '{selector}' sets {property}: {value} on an element that is not declared flat (border-radius: 0).";
                if (property == "box-shadow" && HasEdgeAccent(value) && !flat)
                    yield return $"{file}: '{selector}' draws a single-edge shadow ({value}) without border-radius: 0.";
            }
        }
    }

    // A rail is a side border thicker than a 1px divider.
    private static bool IsRail(string value) =>
        LengthToken().Matches(value).Any(match => double.TryParse(match.Value.TrimEnd('p', 'x', 'r', 'e', 'm'),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width) && width >= 2);

    private static bool IsZero(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(part => part is "0" or "0px" or "0rem" or "0%");

    private static bool HasEdgeAccent(string shadow)
    {
        foreach (var layer in shadow.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!layer.Contains("inset", StringComparison.Ordinal)) continue;
            var lengths = LengthToken().Matches(layer).Select(match => match.Value).ToList();
            if (lengths.Count < 2) continue;
            var x = !IsZero(lengths[0]);
            var y = !IsZero(lengths[1]);
            var blur = lengths.Count > 2 && !IsZero(lengths[2]);
            if (x != y && !blur) return true;
        }

        return false;
    }

    [GeneratedRegex(@"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex RuleBlock();

    [GeneratedRegex(@"^border-(left|right|top|bottom|inline-start|inline-end|block-start|block-end)(-width)?$")]
    private static partial Regex SingleSideBorder();

    [GeneratedRegex(@"(?<![\w-])-?\d+(\.\d+)?(px|rem|em)?(?![\w(-])")]
    private static partial Regex LengthToken();
}
