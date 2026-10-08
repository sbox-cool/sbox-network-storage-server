using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Owner;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// <c>sbox-ns admin login-link</c> tokens: hashed at rest, single use, expiring, never consumed by GET,
/// antiforgery-protected and rate limited. Each test gets its own host and database.
/// </summary>
public abstract class OwnerLoginLinkTests<TFactory> : IDisposable
    where TFactory : SelfHostFactory, new()
{
    private readonly TFactory factory = new();

    protected OwnerLoginLinkTests() => Skip.IfNot(factory.IsAvailable, factory.SkipReason);

    public void Dispose() => factory.Dispose();

    [SkippableFact]
    public async Task OnlyTheTokenHashIsPersisted()
    {
        var token = await MintLoginLinkAsync(factory);
        Assert.Matches("^[0-9a-f]{64}$", token);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var entries = await store.ListWorkspaceObjectsAsync(OwnerLoginLinkService.DirectoryPath, CancellationToken.None);
        var entry = Assert.Single(entries);
        Assert.Equal(OwnerLoginLinkService.Hash(token) + ".json", entry.Name);
        Assert.DoesNotContain(token, entry.Name);
        var content = await store.ReadWorkspaceObjectAsync(OwnerLoginLinkService.ObjectPath(OwnerLoginLinkService.Hash(token)), CancellationToken.None);
        Assert.NotNull(content);
        Assert.DoesNotContain(token, content);
        var record = JsonSerializer.Deserialize<OwnerLoginLinkRecord>(content!)!;
        Assert.InRange(record.ExpiresAt - record.CreatedAt, TimeSpan.FromMinutes(14.9), TimeSpan.FromMinutes(15.1));
    }

    [SkippableFact]
    public async Task LifetimeIsBoundedToAnHour()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<OwnerLoginLinkService>();
        await Assert.ThrowsAsync<ArgumentException>(() => links.CreateAsync(0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => links.CreateAsync(OwnerLoginLinkService.MaxMinutes + 1, CancellationToken.None));
        var (_, expiresAt) = await links.CreateAsync(OwnerLoginLinkService.MaxMinutes, CancellationToken.None);
        Assert.True(expiresAt <= DateTimeOffset.UtcNow.AddMinutes(OwnerLoginLinkService.MaxMinutes));
    }

    [SkippableFact]
    public async Task GetShowsConfirmationWithoutConsumingAndPostSignsInExactlyOnce()
    {
        await CreateOwnerAsync(factory);
        var token = await MintLoginLinkAsync(factory);
        using var preview = Client(factory);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await preview.GetAsync("/login/link?token=" + token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
        Assert.Equal(HttpStatusCode.Redirect, (await preview.GetAsync("/dashboard")).StatusCode);

        using var browser = Client(factory);
        var page = await browser.GetStringAsync("/login/link?token=" + token);
        Assert.Contains("sign in as <strong>", page);
        using var signedIn = await browser.PostAsync("/login/link", Form(("token", token), ("__RequestVerificationToken", Csrf(page))));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/dashboard", signedIn.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/dashboard")).StatusCode);

        using var replay = Client(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await replay.GetAsync("/login/link?token=" + token)).StatusCode);
        using var replayPost = await replay.PostAsync("/login/link", Form(("token", token), ("__RequestVerificationToken", Csrf(await replay.GetStringAsync("/login")))));
        Assert.Equal(HttpStatusCode.NotFound, replayPost.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await replay.GetAsync("/dashboard")).StatusCode);
    }

    [SkippableFact]
    public async Task PostWithoutAntiforgeryIsRejectedAndLeavesTheLinkUsable()
    {
        await CreateOwnerAsync(factory);
        var token = await MintLoginLinkAsync(factory);
        using var client = Client(factory);
        using var forged = await client.PostAsync("/login/link", Form(("token", token)));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.True(await IsValidAsync(token));
    }

    [SkippableFact]
    public async Task WrongMalformedAndExpiredTokensAreRejected()
    {
        await CreateOwnerAsync(factory);
        await MintLoginLinkAsync(factory);
        var expired = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var past = DateTimeOffset.UtcNow.AddMinutes(-30);
            await scope.ServiceProvider.GetRequiredService<INetworkStorageStore>().PutWorkspaceObjectAsync(
                OwnerLoginLinkService.ObjectPath(OwnerLoginLinkService.Hash(expired)),
                JsonSerializer.Serialize(new OwnerLoginLinkRecord(past, past.AddMinutes(15))), CancellationToken.None);
        }
        var wrong = new string('a', 64);
        using var client = Client(factory);
        foreach (var token in new[] { wrong, expired, "not-a-token", wrong.ToUpperInvariant() })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/login/link?token=" + token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/login/link")).StatusCode);

        var csrf = Csrf(await client.GetStringAsync("/login"));
        foreach (var token in new[] { wrong, expired })
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/login/link", Form(("token", token), ("__RequestVerificationToken", csrf)))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/dashboard")).StatusCode);
        Assert.False(await IsValidAsync(expired));
    }

    [SkippableFact]
    public async Task MintingPurgesExpiredLinks()
    {
        var expired = new string('b', 64);
        var path = OwnerLoginLinkService.ObjectPath(OwnerLoginLinkService.Hash(expired));
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var past = DateTimeOffset.UtcNow.AddHours(-2);
        await store.PutWorkspaceObjectAsync(path, JsonSerializer.Serialize(new OwnerLoginLinkRecord(past, past.AddMinutes(15))), CancellationToken.None);
        await MintLoginLinkAsync(factory);
        Assert.Null(await store.ReadWorkspaceObjectAsync(path, CancellationToken.None));
    }

    [SkippableFact]
    public async Task WithoutOwnerTheLinkCreatesTheOwnerFromAnyAddress()
    {
        var token = await MintLoginLinkAsync(factory);
        using var client = Client(factory);
        var page = await client.GetStringAsync("/login/link?token=" + token);
        Assert.Contains("Create owner and sign in", page);
        var csrf = Csrf(page);

        using var mismatch = await client.PostAsync("/login/link", Form(("token", token), ("username", "remote-owner"),
            ("password", "remote-owner-password"), ("confirmPassword", "different-password"), ("__RequestVerificationToken", csrf)));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.True(await IsValidAsync(token));

        using var created = await client.PostAsync("/login/link", Form(("token", token), ("username", "remote-owner"),
            ("password", "remote-owner-password"), ("confirmPassword", "remote-owner-password"), ("__RequestVerificationToken", csrf)));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard")).StatusCode);
        Assert.False(await IsValidAsync(token));
        await using var scope = factory.Services.CreateAsyncScope();
        var owner = await scope.ServiceProvider.GetRequiredService<OwnerAccountService>().GetAsync(CancellationToken.None);
        Assert.Equal("remote-owner", owner?.Username);
    }

    [SkippableFact]
    public async Task LoginLinkIsRateLimitedLikePasswordLogin()
    {
        using var client = Client(factory);
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 25; attempt++)
            statuses.Add((await client.GetAsync("/login/link?token=" + new string('c', 64))).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    private async Task<bool> IsValidAsync(string token)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OwnerLoginLinkService>().IsValidAsync(token, CancellationToken.None);
    }
}

public sealed class SqliteOwnerLoginLinkTests : OwnerLoginLinkTests<SqliteHostFactory>
{
}

public sealed class PostgresOwnerLoginLinkTests : OwnerLoginLinkTests<PostgresHostFactory>
{
}
