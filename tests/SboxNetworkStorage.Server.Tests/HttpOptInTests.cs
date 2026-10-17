using SboxNetworkStorage.Server.Cli;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Plain-HTTP owner sign-in is opt-in: only an operator with no domain/sboxns.com name and no SSH tunnel is
/// offered it, and the flag answers yes without asking.
/// </summary>
public sealed class HttpOptInTests
{
    private static (bool Result, List<string> Asked, string Output) Run(bool current = false, bool flag = false,
        bool httpsConfigured = false, bool interactive = true, params bool[] answers)
    {
        var asked = new List<string>();
        var queue = new Queue<bool>(answers);
        var output = new StringWriter();
        var result = HttpOptIn.Decide(current, flag, httpsConfigured, interactive, 8080, output, (question, _) =>
        {
            asked.Add(question);
            return queue.Dequeue();
        });
        return (result, asked, output.ToString());
    }

    [Fact]
    public void FlagOptsInWithoutAsking()
    {
        var (result, asked, _) = Run(flag: true);
        Assert.True(result);
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HttpsAlreadyConfiguredOrNonInteractiveKeepsTheCurrentValue(bool current)
    {
        Assert.Equal(current, Run(current, httpsConfigured: true).Result);
        var (result, asked, _) = Run(current, interactive: false);
        Assert.Equal(current, result);
        Assert.Empty(asked);
    }

    [Fact]
    public void HavingADomainOrAnSshTunnelLeavesHttpOff()
    {
        var (domain, askedDomain, domainOutput) = Run(answers: [true]);
        Assert.False(domain);
        Assert.Single(askedDomain);
        Assert.Contains("sbox-ns dns enable", domainOutput);

        var (ssh, askedSsh, sshOutput) = Run(answers: [false, true]);
        Assert.False(ssh);
        Assert.Equal(2, askedSsh.Count);
        Assert.Contains("ssh -L 8080:127.0.0.1:8080", sshOutput);

        var (kept, _, keptOutput) = Run(current: true, answers: [true]);
        Assert.True(kept);
        Assert.Contains("still enabled", keptOutput);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithNeitherTheOperatorDecidesAfterTheExplanation(bool accept)
    {
        var (result, asked, output) = Run(answers: [false, false, accept]);
        Assert.Equal(accept, result);
        Assert.Equal(3, asked.Count);
        Assert.Contains("development", output);
        Assert.Contains("recommend switching to HTTPS", output);
        Assert.Contains("dismissable warning", output);
    }
}
