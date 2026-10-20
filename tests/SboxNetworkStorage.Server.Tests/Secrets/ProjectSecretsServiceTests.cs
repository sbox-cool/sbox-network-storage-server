using System.Text;
using Microsoft.Extensions.Configuration;
using SboxNetworkStorage.Application.NetworkStorage.Secrets;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Secrets;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.Secrets;

public sealed class ProjectSecretsServiceTests
{
    private const string KeyA = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string KeyB = "2222222222222222222222222222222222222222222222222222222222222222";

    [Theory]
    [InlineData("A")]
    [InlineData("API_KEY")]
    [InlineData("a.b-c_9")]
    [InlineData("0start")]
    [InlineData("Case")]
    public void Names_that_follow_the_rules_are_accepted(string name) =>
        Assert.True(ProjectSecretsService.IsValidName(name));

    [Fact]
    public void A_64_character_name_is_accepted_and_65_is_rejected()
    {
        Assert.True(ProjectSecretsService.IsValidName(new string('a', 64)));
        Assert.False(ProjectSecretsService.IsValidName(new string('a', 65)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("_leading")]
    [InlineData("-leading")]
    [InlineData(".leading")]
    [InlineData("has space")]
    [InlineData("trailing\n")]
    [InlineData("unicodé")]
    [InlineData("slash/name")]
    public void Names_that_break_the_rules_are_rejected(string name) =>
        Assert.False(ProjectSecretsService.IsValidName(name));

    [Fact]
    public void Null_name_is_rejected() => Assert.False(ProjectSecretsService.IsValidName(null));

    [Fact]
    public async Task Set_then_read_round_trips_and_the_store_never_sees_plaintext()
    {
        var store = new FakeProjectSecretStore();
        var service = Service(store, KeyA);

        Assert.Equal(ProjectSecretSetOutcome.Created, await service.SetAsync("proj_a", "API_KEY", "hunter2-plaintext", CancellationToken.None));
        Assert.Equal("hunter2-plaintext", await service.ReadAsync("proj_a", "API_KEY", CancellationToken.None));

        var stored = store.Rows[("proj_a", "API_KEY")].Sealed;
        Assert.DoesNotContain("hunter2-plaintext", stored);
    }

    [Fact]
    public async Task Replacing_a_value_reports_replaced_and_updates_the_timestamp()
    {
        var store = new FakeProjectSecretStore();
        var clock = new FixedClock(1_000);
        var service = new ProjectSecretsService(store, new ProjectSecretCipher(Config(KeyA)), clock);

        await service.SetAsync("proj_a", "K", "one", CancellationToken.None);
        clock.Now = 2_000;
        Assert.Equal(ProjectSecretSetOutcome.Replaced, await service.SetAsync("proj_a", "K", "two", CancellationToken.None));
        Assert.Equal("two", await service.ReadAsync("proj_a", "K", CancellationToken.None));
        Assert.Equal(new[] { new ProjectSecretSummary("K", 2_000) }, await service.ListAsync("proj_a", CancellationToken.None));
    }

    [Fact]
    public async Task Unset_names_read_as_null_and_values_are_scoped_to_their_project()
    {
        var service = Service(new FakeProjectSecretStore(), KeyA);
        await service.SetAsync("proj_a", "K", "value", CancellationToken.None);

        Assert.Null(await service.ReadAsync("proj_a", "missing", CancellationToken.None));
        Assert.Null(await service.ReadAsync("proj_b", "K", CancellationToken.None));
    }

    [Fact]
    public async Task Unset_names_read_as_null_even_without_a_key()
    {
        var service = Service(new FakeProjectSecretStore(), key: null);
        Assert.False(service.IsAvailable);
        Assert.Null(await service.ReadAsync("proj_a", "K", CancellationToken.None));
    }

    [Fact]
    public async Task Writing_without_a_key_is_refused_and_stores_nothing()
    {
        var store = new FakeProjectSecretStore();
        var service = Service(store, key: null);

        await Assert.ThrowsAsync<ProjectSecretUnavailableException>(() => service.SetAsync("proj_a", "K", "value", CancellationToken.None));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Reading_a_stored_value_without_a_key_fails_closed()
    {
        var store = new FakeProjectSecretStore();
        await Service(store, KeyA).SetAsync("proj_a", "K", "value", CancellationToken.None);

        var keyless = Service(store, key: null);
        await Assert.ThrowsAsync<ProjectSecretUnavailableException>(() => keyless.ReadAsync("proj_a", "K", CancellationToken.None));
    }

    [Fact]
    public async Task A_value_sealed_under_a_different_key_fails_closed()
    {
        var store = new FakeProjectSecretStore();
        await Service(store, KeyA).SetAsync("proj_a", "K", "value", CancellationToken.None);

        var otherKey = Service(store, KeyB);
        await Assert.ThrowsAsync<ProjectSecretUnavailableException>(() => otherKey.ReadAsync("proj_a", "K", CancellationToken.None));
    }

    [Fact]
    public void A_sealed_value_is_bound_to_its_project_and_name()
    {
        var cipher = new ProjectSecretCipher(Config(KeyA));
        var token = cipher.Seal("proj_a", "K", "value");

        Assert.Equal("value", cipher.Open("proj_a", "K", token));
        Assert.Throws<ProjectSecretUnavailableException>(() => cipher.Open("proj_b", "K", token));
        Assert.Throws<ProjectSecretUnavailableException>(() => cipher.Open("proj_a", "OTHER", token));
    }

    [Fact]
    public void Each_seal_uses_a_fresh_nonce()
    {
        var cipher = new ProjectSecretCipher(Config(KeyA));
        Assert.NotEqual(cipher.Seal("proj_a", "K", "same"), cipher.Seal("proj_a", "K", "same"));
    }

    [Fact]
    public void A_tampered_or_malformed_token_fails_closed()
    {
        var cipher = new ProjectSecretCipher(Config(KeyA));
        var bytes = Convert.FromBase64String(cipher.Seal("proj_a", "K", "value"));
        bytes[^1] ^= 0x01;

        Assert.Throws<ProjectSecretUnavailableException>(() => cipher.Open("proj_a", "K", Convert.ToBase64String(bytes)));
        Assert.Throws<ProjectSecretUnavailableException>(() => cipher.Open("proj_a", "K", "not base64!"));
        Assert.Throws<ProjectSecretUnavailableException>(() => cipher.Open("proj_a", "K", Convert.ToBase64String([2, 0, 0])));
    }

    [Fact]
    public async Task Empty_and_oversized_values_are_rejected_before_anything_is_stored()
    {
        var store = new FakeProjectSecretStore();
        var service = Service(store, KeyA);

        await Assert.ThrowsAsync<ProjectSecretValidationException>(() => service.SetAsync("proj_a", "K", "", CancellationToken.None));
        await Assert.ThrowsAsync<ProjectSecretValidationException>(() => service.SetAsync("proj_a", "K", new string('x', 4097), CancellationToken.None));
        // Multi-byte: 2049 two-byte characters are 4098 UTF-8 bytes.
        await Assert.ThrowsAsync<ProjectSecretValidationException>(() => service.SetAsync("proj_a", "K", new string('é', 2049), CancellationToken.None));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task A_value_of_exactly_4096_bytes_is_accepted()
    {
        var service = Service(new FakeProjectSecretStore(), KeyA);
        var value = new string('x', 4096);
        await service.SetAsync("proj_a", "K", value, CancellationToken.None);
        Assert.Equal(value, await service.ReadAsync("proj_a", "K", CancellationToken.None));
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(value));
    }

    [Fact]
    public async Task Invalid_names_are_rejected_before_anything_is_stored()
    {
        var store = new FakeProjectSecretStore();
        await Assert.ThrowsAsync<ProjectSecretValidationException>(() => Service(store, KeyA).SetAsync("proj_a", "bad name", "v", CancellationToken.None));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task The_64_secret_cap_refuses_a_new_name_but_still_replaces_an_existing_one()
    {
        var store = new FakeProjectSecretStore();
        var service = Service(store, KeyA);
        for (var i = 0; i < ProjectSecretsService.MaxSecretsPerProject; i++)
            await service.SetAsync("proj_a", $"S{i}", "v", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ProjectSecretValidationException>(() => service.SetAsync("proj_a", "one_more", "v", CancellationToken.None));
        Assert.Contains("64", ex.Message);
        Assert.Equal(ProjectSecretSetOutcome.Replaced, await service.SetAsync("proj_a", "S0", "new", CancellationToken.None));
        Assert.Equal(ProjectSecretsService.MaxSecretsPerProject, store.Rows.Count(r => r.Key.Item1 == "proj_a"));
    }

    [Fact]
    public async Task Delete_reports_whether_the_name_existed()
    {
        var service = Service(new FakeProjectSecretStore(), KeyA);
        await service.SetAsync("proj_a", "K", "v", CancellationToken.None);

        Assert.True(await service.DeleteAsync("proj_a", "K", CancellationToken.None));
        Assert.False(await service.DeleteAsync("proj_a", "K", CancellationToken.None));
        Assert.Null(await service.ReadAsync("proj_a", "K", CancellationToken.None));
    }

    private static ProjectSecretsService Service(FakeProjectSecretStore store, string? key) =>
        new(store, new ProjectSecretCipher(Config(key)), new FixedClock(1_000));

    private static IConfiguration Config(string? key) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(key is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["STORAGE_ENCRYPTION_KEY"] = key })
            .Build();

    private sealed class FixedClock(long unixMs) : TimeProvider
    {
        public long Now { get; set; } = unixMs;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    private sealed class FakeProjectSecretStore : IProjectSecretStore
    {
        public Dictionary<(string, string), (string Sealed, long UpdatedAt)> Rows { get; } = new();

        public Task<IReadOnlyList<ProjectSecretSummary>> ListProjectSecretsAsync(string projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProjectSecretSummary>>(Rows
                .Where(r => r.Key.Item1 == projectId)
                .OrderBy(r => r.Key.Item2, StringComparer.Ordinal)
                .Select(r => new ProjectSecretSummary(r.Key.Item2, r.Value.UpdatedAt))
                .ToList());

        public Task<string?> GetProjectSecretSealedAsync(string projectId, string name, CancellationToken ct) =>
            Task.FromResult(Rows.TryGetValue((projectId, name), out var row) ? row.Sealed : null);

        public Task<ProjectSecretWriteResult> SetProjectSecretAsync(string projectId, string name, string sealedValue, long updatedAtUnixMs, int maxSecrets, CancellationToken ct)
        {
            if (Rows.ContainsKey((projectId, name)))
            {
                Rows[(projectId, name)] = (sealedValue, updatedAtUnixMs);
                return Task.FromResult(ProjectSecretWriteResult.Replaced);
            }
            if (Rows.Count(r => r.Key.Item1 == projectId) >= maxSecrets)
                return Task.FromResult(ProjectSecretWriteResult.LimitReached);
            Rows[(projectId, name)] = (sealedValue, updatedAtUnixMs);
            return Task.FromResult(ProjectSecretWriteResult.Created);
        }

        public Task<bool> DeleteProjectSecretAsync(string projectId, string name, CancellationToken ct) =>
            Task.FromResult(Rows.Remove((projectId, name)));
    }
}
