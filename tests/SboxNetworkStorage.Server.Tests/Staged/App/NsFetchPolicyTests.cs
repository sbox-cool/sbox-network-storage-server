using System.Net;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

namespace SboxNetworkStorage.Server.Tests;

public sealed class NsFetchPolicyTests
{
    private static readonly EndpointFetchPolicy ApiOnly = new(["api.example.com"], false);
    private static readonly JsonElement NoOptions = default;

    [Theory]
    [InlineData("api.example.com", "api.example.com")]
    [InlineData("  API.Example.COM  ", "api.example.com")]
    [InlineData("api.example.com.", "api.example.com")]
    [InlineData("*.example.com", "*.example.com")]
    [InlineData("*.Example.com.", "*.example.com")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("2001:DB8::1", "2001:db8::1")]
    public void TryNormalizeEntry_ValidEntry_LowercasesTrimsTrailingDotAndKeepsWildcardAndIpForms(string raw, string expected)
    {
        Assert.True(FetchHostRules.TryNormalizeEntry(raw, out var entry));
        Assert.Equal(expected, entry);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://api.example.com")]
    [InlineData("http://api.example.com")]
    [InlineData("api.example.com:8443")]
    [InlineData("10.0.0.1:8080")]
    [InlineData("api.example.com/v1")]
    [InlineData("api.example.com?x=1")]
    [InlineData("api example.com")]
    [InlineData("api..example.com")]
    [InlineData("-api.example.com")]
    [InlineData("api_v1.example.com")]
    [InlineData("10.0.0.256")]
    [InlineData("*")]
    [InlineData("*.")]
    [InlineData("*.com")]
    [InlineData("*.*.example.com")]
    [InlineData("example.*.com")]
    [InlineData("*example.com")]
    [InlineData("*.example.*")]
    public void TryNormalizeEntry_InvalidEntry_IsRejectedWithEmptyEntry(string raw)
    {
        Assert.False(FetchHostRules.TryNormalizeEntry(raw, out var entry));
        Assert.Equal(string.Empty, entry);
    }

    [Fact]
    public void TryNormalizeEntry_NullInput_IsRejected()
    {
        Assert.False(FetchHostRules.TryNormalizeEntry(null, out var entry));
        Assert.Equal(string.Empty, entry);
    }

    [Fact]
    public void TryNormalizeEntry_HostLongerThan253Characters_IsRejected()
    {
        var host = string.Join('.', Enumerable.Repeat(new string('a', 63), 4)); // 255 characters

        Assert.False(FetchHostRules.TryNormalizeEntry(host, out _));
    }

    [Fact]
    public void TryNormalizeEntry_HostOf253CharactersWithValidLabels_IsAccepted()
    {
        var host = string.Join('.', new[] { new string('a', 63), new string('a', 63), new string('a', 63), new string('a', 61) });

        Assert.True(FetchHostRules.TryNormalizeEntry(host, out var entry));
        Assert.Equal(host, entry);
    }

    [Fact]
    public void TryNormalizeEntry_LabelLongerThan63Characters_IsRejected()
    {
        Assert.False(FetchHostRules.TryNormalizeEntry(new string('a', 64) + ".example.com", out _));
    }

    [Theory]
    [InlineData("API.Example.com.", "api.example.com")]
    [InlineData("[::1]", "::1")]
    [InlineData("[2001:DB8::1]", "2001:db8::1")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    public void NormalizeHost_StripsIpv6BracketsAndTrailingDotAndLowercases(string host, string expected)
    {
        Assert.Equal(expected, FetchHostRules.NormalizeHost(host));
    }

    [Fact]
    public void IsAllowed_ExactEntry_MatchesOnlyThatHost()
    {
        var entries = new[] { "api.example.com" };

        Assert.True(FetchHostRules.IsAllowed("api.example.com", entries));
        Assert.False(FetchHostRules.IsAllowed("v2.api.example.com", entries));
        Assert.False(FetchHostRules.IsAllowed("example.com", entries));
    }

    [Fact]
    public void IsAllowed_WildcardEntry_MatchesOneLevelSubdomain()
    {
        Assert.True(FetchHostRules.IsAllowed("a.example.com", ["*.example.com"]));
    }

    [Fact]
    public void IsAllowed_WildcardEntry_MatchesNestedSubdomain()
    {
        Assert.True(FetchHostRules.IsAllowed("a.b.example.com", ["*.example.com"]));
    }

    [Fact]
    public void IsAllowed_WildcardEntry_DoesNotMatchApex()
    {
        Assert.False(FetchHostRules.IsAllowed("example.com", ["*.example.com"]));
    }

    [Theory]
    [InlineData("badexample.com")]
    [InlineData("evilexample.com")]
    [InlineData("example.com.evil.net")]
    public void IsAllowed_WildcardEntry_DoesNotMatchSuffixLookalikeHosts(string host)
    {
        Assert.False(FetchHostRules.IsAllowed(host, ["*.example.com"]));
    }

    [Fact]
    public void IsAllowed_NormalizedHostAndEntries_MatchRegardlessOfCase()
    {
        Assert.True(FetchHostRules.TryNormalizeEntry("API.Example.com", out var exact));
        Assert.True(FetchHostRules.TryNormalizeEntry("*.CDN.Example.com", out var wildcard));
        var entries = new[] { exact, wildcard };

        Assert.True(FetchHostRules.IsAllowed(FetchHostRules.NormalizeHost("Api.EXAMPLE.com"), entries));
        Assert.True(FetchHostRules.IsAllowed(FetchHostRules.NormalizeHost("Img.Cdn.EXAMPLE.com"), entries));
    }

    [Fact]
    public void IsAllowed_IpLiteralEntries_MatchExactAddressOnly()
    {
        var entries = new[] { "8.8.8.8", "2606:4700:4700::1111" };

        Assert.True(FetchHostRules.IsAllowed("8.8.8.8", entries));
        Assert.True(FetchHostRules.IsAllowed("2606:4700:4700::1111", entries));
        Assert.False(FetchHostRules.IsAllowed("8.8.8.80", entries));
        Assert.False(FetchHostRules.IsAllowed("2606:4700:4700::1112", entries));
    }

    [Fact]
    public void IsAllowed_EmptyAllowList_RefusesEveryHost()
    {
        Assert.False(FetchHostRules.IsAllowed("api.example.com", []));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.10.20")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456::1")]
    public void BlockedReason_LoopbackLinkLocalAndPrivateRanges_ReturnsPrivateAddress(string address)
    {
        Assert.Equal(FetchAddressRules.PrivateAddress, ReasonFor(address, allowPrivateNetworks: false));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.10.20")]
    [InlineData("::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:10.0.0.5")]
    public void BlockedReason_PrivateRangesWithPrivateNetworksAllowed_ReturnsNull(string address)
    {
        Assert.Null(ReasonFor(address, allowPrivateNetworks: true));
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.170.2")]
    [InlineData("100.100.100.200")]
    [InlineData("fd00:ec2::254")]
    public void BlockedReason_CloudMetadataAddress_IsAlwaysMetadataAddress(string address)
    {
        Assert.Equal(FetchAddressRules.MetadataAddress, ReasonFor(address, allowPrivateNetworks: false));
        Assert.Equal(FetchAddressRules.MetadataAddress, ReasonFor(address, allowPrivateNetworks: true));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.10")]
    [InlineData("198.18.0.1")]
    [InlineData("203.0.113.7")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("ff02::1")]
    public void BlockedReason_ReservedAndMulticastAddress_IsAlwaysReservedAddress(string address)
    {
        Assert.Equal(FetchAddressRules.ReservedAddress, ReasonFor(address, allowPrivateNetworks: false));
        Assert.Equal(FetchAddressRules.ReservedAddress, ReasonFor(address, allowPrivateNetworks: true));
    }

    [Theory]
    [InlineData("::ffff:10.0.0.5", FetchAddressRules.PrivateAddress)]
    [InlineData("::ffff:169.254.169.254", FetchAddressRules.MetadataAddress)]
    [InlineData("::ffff:224.0.0.1", FetchAddressRules.ReservedAddress)]
    [InlineData("64:ff9b::a00:1", FetchAddressRules.PrivateAddress)]
    [InlineData("64:ff9b::a9fe:a9fe", FetchAddressRules.MetadataAddress)]
    [InlineData("2002:a00:1::", FetchAddressRules.PrivateAddress)]
    public void BlockedReason_Ipv4EmbeddedInIpv6_ChecksTheEmbeddedIpv4Address(string address, string expected)
    {
        Assert.Equal(expected, ReasonFor(address, allowPrivateNetworks: false));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::")]
    public void BlockedReason_PublicAddress_ReturnsNull(string address)
    {
        Assert.Null(ReasonFor(address, allowPrivateNetworks: false));
    }

    [Fact]
    public void ReasonCodes_UseOwnerFacingWireValues()
    {
        Assert.Equal("private_address", FetchAddressRules.PrivateAddress);
        Assert.Equal("metadata_address", FetchAddressRules.MetadataAddress);
        Assert.Equal("reserved_address", FetchAddressRules.ReservedAddress);
    }

    [Fact]
    public void Call_AllowListedHost_QueuesRequestAndReturnsNullUntilLoaded()
    {
        var session = new ScriptFetchSession(ApiOnly, new FakeFetchTransport());

        Assert.Null(session.Call("https://api.example.com/v1/items", NoOptions));

        Assert.Equal(1, session.PendingCount);
        Assert.Empty(session.Blocked);
    }

    [Fact]
    public void Call_AllowListedHostWithExplicitPort_IsNotRefused()
    {
        var session = new ScriptFetchSession(ApiOnly, new FakeFetchTransport());

        Assert.Null(session.Call("https://api.example.com:8443/status", NoOptions));

        Assert.Equal(1, session.PendingCount);
        Assert.Empty(session.Blocked);
    }

    [Fact]
    public void Call_HostNotOnAllowList_ThrowsFetchBlockedAndRecordsHostAndReason()
    {
        var session = new ScriptFetchSession(ApiOnly, new FakeFetchTransport());

        var error = Assert.Throws<ScriptFetchException>(() => session.Call("https://other.example.net/x", NoOptions));

        Assert.Equal("FETCH_BLOCKED", error.Code);
        Assert.Equal(new EndpointFetchBlockedCall("other.example.net", "host_not_allowed"), Assert.Single(session.Blocked));
        Assert.Equal(0, session.PendingCount);
    }

    [Fact]
    public void Call_WildcardAllowListEntry_QueuesSubdomainButRefusesApex()
    {
        var session = new ScriptFetchSession(new EndpointFetchPolicy(["*.example.com"], false), new FakeFetchTransport());

        Assert.Null(session.Call("https://a.b.example.com/x", NoOptions));
        var error = Assert.Throws<ScriptFetchException>(() => session.Call("https://example.com/x", NoOptions));

        Assert.Equal("FETCH_BLOCKED", error.Code);
        Assert.Equal(new EndpointFetchBlockedCall("example.com", "host_not_allowed"), Assert.Single(session.Blocked));
    }

    [Fact]
    public void Call_IpLiteralAllowListEntry_MatchesExactlyAndRefusesNeighbours()
    {
        var session = new ScriptFetchSession(new EndpointFetchPolicy(["8.8.8.8"], false), new FakeFetchTransport());

        Assert.Null(session.Call("https://8.8.8.8/dns", NoOptions));
        var error = Assert.Throws<ScriptFetchException>(() => session.Call("https://8.8.8.9/dns", NoOptions));

        Assert.Equal("FETCH_BLOCKED", error.Code);
        Assert.Equal(new EndpointFetchBlockedCall("8.8.8.9", "host_not_allowed"), Assert.Single(session.Blocked));
    }

    [Fact]
    public void Call_AllowAllPublic_QueuesHostsOffTheList()
    {
        var session = new ScriptFetchSession(new EndpointFetchPolicy([], true), new FakeFetchTransport());

        Assert.Null(session.Call("https://anything.example.net/x", NoOptions));

        Assert.Empty(session.Blocked);
    }

    [Fact]
    public void Call_AllowAllPublic_StillRefusesPrivateIpLiteral()
    {
        var session = new ScriptFetchSession(new EndpointFetchPolicy([], true), new FakeFetchTransport());

        var error = Assert.Throws<ScriptFetchException>(() => session.Call("http://10.0.0.5/admin", NoOptions));

        Assert.Equal("FETCH_BLOCKED", error.Code);
        Assert.Equal(new EndpointFetchBlockedCall("10.0.0.5", FetchAddressRules.PrivateAddress), Assert.Single(session.Blocked));
    }

    [Fact]
    public void Call_ClosedPolicy_RefusesEveryHost()
    {
        var session = new ScriptFetchSession(EndpointFetchPolicy.Closed, new FakeFetchTransport());

        var error = Assert.Throws<ScriptFetchException>(() => session.Call("https://api.example.com/x", NoOptions));

        Assert.Equal("FETCH_BLOCKED", error.Code);
        Assert.Equal(new EndpointFetchBlockedCall("api.example.com", "host_not_allowed"), Assert.Single(session.Blocked));
    }

    [Fact]
    public void Call_PrivateIpLiteralNotOnAllowList_ReportsPrivateAddressBeforeAllowList()
    {
        var session = new ScriptFetchSession(ApiOnly, new FakeFetchTransport());

        var error = Assert.Throws<ScriptFetchException>(() => session.Call("http://10.0.0.5/admin", NoOptions));

        Assert.Equal("FETCH_BLOCKED", error.Code);
        Assert.Equal(new EndpointFetchBlockedCall("10.0.0.5", FetchAddressRules.PrivateAddress), Assert.Single(session.Blocked));
    }

    [Fact]
    public void Call_PrivateIpLiteralOnAllowListWithPrivateNetworksOff_IsStillRefused()
    {
        var session = new ScriptFetchSession(new EndpointFetchPolicy(["10.0.0.5"], false), new FakeFetchTransport(allowPrivateNetworks: false));

        var error = Assert.Throws<ScriptFetchException>(() => session.Call("http://10.0.0.5/admin", NoOptions));

        Assert.Equal("FETCH_BLOCKED", error.Code);
        Assert.Equal(new EndpointFetchBlockedCall("10.0.0.5", FetchAddressRules.PrivateAddress), Assert.Single(session.Blocked));
    }

    [Fact]
    public void Call_PrivateIpLiteralOnAllowListWithPrivateNetworksOn_IsQueued()
    {
        var session = new ScriptFetchSession(new EndpointFetchPolicy(["10.0.0.5"], false), new FakeFetchTransport(allowPrivateNetworks: true));

        Assert.Null(session.Call("http://10.0.0.5/status", NoOptions));

        Assert.Equal(1, session.PendingCount);
        Assert.Empty(session.Blocked);
    }

    [Fact]
    public void Call_MetadataAddress_IsBlockedEvenWhenPrivateNetworksAreAllowed()
    {
        var session = new ScriptFetchSession(new EndpointFetchPolicy([], true), new FakeFetchTransport(allowPrivateNetworks: true));

        var error = Assert.Throws<ScriptFetchException>(() => session.Call("http://169.254.169.254/latest/meta-data/", NoOptions));

        Assert.Equal("FETCH_BLOCKED", error.Code);
        Assert.Equal(new EndpointFetchBlockedCall("169.254.169.254", FetchAddressRules.MetadataAddress), Assert.Single(session.Blocked));
    }

    [Fact]
    public void Call_PrivateIpv6Literal_IsRefusedAsPrivateAddress()
    {
        var session = new ScriptFetchSession(ApiOnly, new FakeFetchTransport());

        var error = Assert.Throws<ScriptFetchException>(() => session.Call("http://[fd00::1]/status", NoOptions));

        Assert.Equal("FETCH_BLOCKED", error.Code);
        Assert.Equal(new EndpointFetchBlockedCall("fd00::1", FetchAddressRules.PrivateAddress), Assert.Single(session.Blocked));
    }

    [Fact]
    public void Call_SixthRequestInOneExecution_ThrowsFetchLimitExceededAndTracesIt()
    {
        var session = new ScriptFetchSession(ApiOnly, new FakeFetchTransport(), captureTrace: true);
        for (var i = 1; i <= 5; i++)
            Assert.Null(session.Call($"https://api.example.com/items/{i}", NoOptions));

        var error = Assert.Throws<ScriptFetchException>(() => session.Call("https://api.example.com/items/6", NoOptions));

        Assert.Equal("FETCH_LIMIT_EXCEEDED", error.Code);
        Assert.Equal(5, session.PendingCount);
        Assert.Equal(new ScriptFetchTraceEntry(6, "api.example.com", "GET", null, "FETCH_LIMIT_EXCEEDED", null), session.Trace[5]);
    }

    [Fact]
    public async Task Call_CachedRerunCalls_DoNotCountTowardRequestLimit()
    {
        var transport = new FakeFetchTransport();
        var session = new ScriptFetchSession(ApiOnly, transport);

        // Each round re-runs the script: earlier calls return cached responses and the next call is queued.
        for (var round = 1; round <= 5; round++)
        {
            session.BeginRound();
            for (var i = 1; i < round; i++)
                Assert.NotNull(session.Call($"https://api.example.com/items/{i}", NoOptions));
            Assert.Null(session.Call($"https://api.example.com/items/{round}", NoOptions));
            await session.LoadPendingAsync(CancellationToken.None);
        }

        session.BeginRound();
        for (var i = 1; i <= 5; i++)
            Assert.NotNull(session.Call($"https://api.example.com/items/{i}", NoOptions));
        var error = Assert.Throws<ScriptFetchException>(() => session.Call("https://api.example.com/items/6", NoOptions));

        Assert.Equal("FETCH_LIMIT_EXCEEDED", error.Code);
        Assert.Equal(5, transport.Requests.Count);
    }

    [Fact]
    public void Call_PolicyRefusedCalls_DoNotCountTowardRequestLimit()
    {
        var session = new ScriptFetchSession(ApiOnly, new FakeFetchTransport());
        for (var i = 1; i <= 6; i++)
        {
            var error = Assert.Throws<ScriptFetchException>(() => session.Call($"https://other.example.net/{i}", NoOptions));
            Assert.Equal("FETCH_BLOCKED", error.Code);
        }

        for (var i = 1; i <= 5; i++)
            Assert.Null(session.Call($"https://api.example.com/items/{i}", NoOptions));

        Assert.Equal(5, session.PendingCount);
    }

    [Fact]
    public async Task Call_TransportRefusedCalls_CountTowardRequestLimit()
    {
        var transport = new FakeFetchTransport
        {
            Respond = _ => ScriptFetchResult.Fail(new ScriptFetchFailure("FETCH_BLOCKED", FetchAddressRules.PrivateAddress, "api.example.com")),
        };
        var session = new ScriptFetchSession(ApiOnly, transport);
        for (var i = 1; i <= 5; i++)
            Assert.Null(session.Call($"https://api.example.com/items/{i}", NoOptions));
        await session.LoadPendingAsync(CancellationToken.None);

        var error = Assert.Throws<ScriptFetchException>(() => session.Call("https://api.example.com/items/6", NoOptions));

        Assert.Equal("FETCH_LIMIT_EXCEEDED", error.Code);
    }


    [Fact]
    public async Task Call_AfterLoad_ReturnsCachedResponseWithoutSecondRequest()
    {
        var transport = new FakeFetchTransport();
        var session = new ScriptFetchSession(ApiOnly, transport, captureTrace: true);

        Assert.Null(session.Call("https://api.example.com/v1/items", NoOptions));
        await session.LoadPendingAsync(CancellationToken.None);

        session.BeginRound();
        var response = Assert.IsType<Dictionary<string, object?>>(session.Call("https://api.example.com/v1/items", NoOptions));

        Assert.Equal(200L, Assert.IsType<long>(response["status"]));
        Assert.True(Assert.IsType<bool>(response["ok"]));
        Assert.Equal("""{"ok":true}""", response["body"]);
        Assert.Single(transport.Requests);
        Assert.Equal(new ScriptFetchTraceEntry(1, "api.example.com", "GET", 200, null, null), Assert.Single(session.Trace));
    }

    [Fact]
    public async Task Call_TransportFailure_ThrowsFailureCodeOnRerunAndTracesReason()
    {
        var transport = new FakeFetchTransport
        {
            Respond = _ => ScriptFetchResult.Fail(new ScriptFetchFailure("FETCH_TIMEOUT", "timeout", "api.example.com")),
        };
        var session = new ScriptFetchSession(ApiOnly, transport, captureTrace: true);
        Assert.Null(session.Call("https://api.example.com/slow", NoOptions));
        await session.LoadPendingAsync(CancellationToken.None);

        session.BeginRound();
        var error = Assert.Throws<ScriptFetchException>(() => session.Call("https://api.example.com/slow", NoOptions));

        Assert.Equal("FETCH_TIMEOUT", error.Code);
        Assert.Equal(new ScriptFetchTraceEntry(1, "api.example.com", "GET", null, "FETCH_TIMEOUT", "timeout"), Assert.Single(session.Trace));
    }

    [Fact]
    public async Task Call_TransportBlocksResolvedAddress_RecordsFailedHopAndThrowsFetchBlockedOnRerun()
    {
        var transport = new FakeFetchTransport
        {
            Respond = _ => ScriptFetchResult.Fail(new ScriptFetchFailure("FETCH_BLOCKED", FetchAddressRules.PrivateAddress, "internal.example.com")),
        };
        var session = new ScriptFetchSession(new EndpointFetchPolicy(["*.example.com"], false), transport, captureTrace: true);
        Assert.Null(session.Call("https://gateway.example.com/x", NoOptions));
        await session.LoadPendingAsync(CancellationToken.None);

        Assert.Equal(new EndpointFetchBlockedCall("internal.example.com", FetchAddressRules.PrivateAddress), Assert.Single(session.Blocked));
        Assert.Equal(
            new ScriptFetchTraceEntry(1, "internal.example.com", "GET", null, "FETCH_BLOCKED", FetchAddressRules.PrivateAddress),
            Assert.Single(session.Trace));

        session.BeginRound();
        var error = Assert.Throws<ScriptFetchException>(() => session.Call("https://gateway.example.com/x", NoOptions));
        Assert.Equal("FETCH_BLOCKED", error.Code);
    }

    [Fact]
    public void ParseAllowList_TrimsLowercasesAndKeepsFirstSeenOrder()
    {
        var entries = FetchPolicyForm.ParseAllowList("  API.Example.com  \n*.CDN.example.com.\r\n10.0.0.1\n");

        Assert.Equal(new[] { "api.example.com", "*.cdn.example.com", "10.0.0.1" }, entries);
    }

    [Fact]
    public void ParseAllowList_DuplicatesAfterNormalization_CollapseToOneEntry()
    {
        var entries = FetchPolicyForm.ParseAllowList("api.example.com\nAPI.example.com.\n  api.example.com  ");

        Assert.Equal(new[] { "api.example.com" }, entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n   \n\t\n")]
    public void ParseAllowList_NoEntries_ReturnsNull(string? text)
    {
        Assert.Null(FetchPolicyForm.ParseAllowList(text));
    }

    [Theory]
    [InlineData("https://evil.example.net")]
    [InlineData("api.example.com:443")]
    [InlineData("api.example.com/v1")]
    [InlineData("*.com")]
    [InlineData("*")]
    public void ParseAllowList_InvalidEntry_ThrowsValidationException(string line)
    {
        Assert.Throws<FetchPolicyValidationException>(() => FetchPolicyForm.ParseAllowList($"api.example.com\n{line}"));
    }

    [Fact]
    public void ParseAllowList_InvalidEntry_MessageNamesTheRejectedLine()
    {
        var error = Assert.Throws<FetchPolicyValidationException>(() => FetchPolicyForm.ParseAllowList("api.example.com\nhttps://evil.example.net"));

        Assert.Contains("'https://evil.example.net'", error.Message);
    }

    [Fact]
    public void ParseAllowList_ExactlyMaxEntries_IsAccepted()
    {
        var entries = FetchPolicyForm.ParseAllowList(string.Join('\n', Hosts(FetchHostRules.MaxEntries)));

        Assert.NotNull(entries);
        Assert.Equal(FetchHostRules.MaxEntries, entries.Count);
    }

    [Fact]
    public void ParseAllowList_OneMoreThanMaxEntries_ThrowsValidationExceptionNamingTheCap()
    {
        var text = string.Join('\n', Hosts(FetchHostRules.MaxEntries + 1));

        var error = Assert.Throws<FetchPolicyValidationException>(() => FetchPolicyForm.ParseAllowList(text));

        Assert.Contains(FetchHostRules.MaxEntries.ToString(), error.Message);
    }

    [Fact]
    public void ParseAllowList_DuplicatesDoNotCountTowardMaxEntries()
    {
        var text = string.Join('\n', Hosts(FetchHostRules.MaxEntries).Append("HOST1.example.com"));

        var entries = FetchPolicyForm.ParseAllowList(text);

        Assert.NotNull(entries);
        Assert.Equal(FetchHostRules.MaxEntries, entries.Count);
    }

    [Fact]
    public void AddExactHost_NullCurrentList_ReturnsNormalizedHostAlone()
    {
        Assert.Equal(new[] { "api.example.com" }, FetchPolicyForm.AddExactHost(null, "API.Example.com."));
    }

    [Fact]
    public void AddExactHost_ExistingList_AppendsHostAndLeavesInputUnchanged()
    {
        var current = new List<string> { "a.example.com" };

        var result = FetchPolicyForm.AddExactHost(current, "b.example.com");

        Assert.Equal(new[] { "a.example.com", "b.example.com" }, result);
        Assert.Single(current);
    }

    [Fact]
    public void AddExactHost_CalledTwiceWithSameHost_IsIdempotent()
    {
        var once = FetchPolicyForm.AddExactHost(null, "api.example.com");

        var twice = FetchPolicyForm.AddExactHost(once, "API.example.com");

        Assert.Equal(new[] { "api.example.com" }, twice);
    }

    [Theory]
    [InlineData("*.example.com")]
    [InlineData("*.Example.com.")]
    public void AddExactHost_Wildcard_ThrowsValidationException(string host)
    {
        Assert.Throws<FetchPolicyValidationException>(() => FetchPolicyForm.AddExactHost(null, host));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://api.example.com")]
    [InlineData("api.example.com:443")]
    public void AddExactHost_InvalidHost_ThrowsValidationException(string? host)
    {
        Assert.Throws<FetchPolicyValidationException>(() => FetchPolicyForm.AddExactHost(null, host));
    }

    [Fact]
    public void AddExactHost_ListAtMaxEntries_NewHostThrowsValidationException()
    {
        var full = Hosts(FetchHostRules.MaxEntries);

        Assert.Throws<FetchPolicyValidationException>(() => FetchPolicyForm.AddExactHost(full, "new.example.com"));
    }

    [Fact]
    public void AddExactHost_ListAtMaxEntries_AlreadyListedHostReturnsListUnchanged()
    {
        var full = Hosts(FetchHostRules.MaxEntries);

        var result = FetchPolicyForm.AddExactHost(full, "host7.example.com");

        Assert.Equal(full, result);
    }

    [Fact]
    public void AddExactHost_ListOneBelowMaxEntries_AddsHostUpToTheCap()
    {
        var result = FetchPolicyForm.AddExactHost(Hosts(FetchHostRules.MaxEntries - 1), "new.example.com");

        Assert.Equal(FetchHostRules.MaxEntries, result.Count);
        Assert.Equal("new.example.com", result[^1]);
    }

    private static string? ReasonFor(string address, bool allowPrivateNetworks) =>
        FetchAddressRules.BlockedReason(IPAddress.Parse(address), allowPrivateNetworks);

    private static List<string> Hosts(int count) =>
        Enumerable.Range(1, count).Select(i => $"host{i}.example.com").ToList();

    private sealed class FakeFetchTransport(bool allowPrivateNetworks = false) : IScriptFetchTransport
    {
        public bool AllowPrivateNetworks => allowPrivateNetworks;

        public List<ScriptFetchRequest> Requests { get; } = [];

        public Func<ScriptFetchRequest, ScriptFetchResult> Respond { get; set; } = _ => OkResponse();

        public Task<ScriptFetchResult> SendAsync(ScriptFetchRequest request, EndpointFetchPolicy policy, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }

        private static ScriptFetchResult OkResponse() =>
            ScriptFetchResult.Ok(new ScriptFetchResponse(200, new Dictionary<string, string>(), """{"ok":true}"""));
    }
}
