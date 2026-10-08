using System.Text.Json;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

public sealed class InMemoryProjectImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReplayLeavesNoRowsAndCorrectedRetryCanClaimId(bool cancel)
    {
        var store = new InMemoryNetworkStorageStore();
        using var cancellation = new CancellationTokenSource();
        var payload = JsonSerializer.SerializeToElement(new { name = "Portable" });
        async Task RestoreAsync(INetworkStorageStore target, CancellationToken ct)
        {
            await target.UpsertProjectAsync("portable", payload, 1, ct);
            await target.UpsertCollectionAsync("portable", "inventory", "Inventory", "private", payload, 3, ct);
            await target.UpsertRecordAsync("portable", "inventory", "player", payload, false, 5, ct);
            await target.UpsertProjectMembershipAsync("1", "portable", "owner", 0, ct);
            await target.PutWorkspaceObjectAsync("network-storage/users/1/portable/package.json", "{}", ct);
            if (cancel) cancellation.Cancel();
            else await target.UpsertCollectionAsync("portable", "__sbox_invalid", "Bad", "private", payload, 1, ct);
        }
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.TryImportProjectAsync("portable", RestoreAsync, cancellation.Token));
        else
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.TryImportProjectAsync("portable", RestoreAsync, cancellation.Token));
        Assert.Empty(store.Projects);
        Assert.Empty(store.Collections);
        Assert.Empty(store.Records);
        Assert.Empty(store.ProjectMembers);
        Assert.Empty(store.WorkspaceObjects);

        Assert.True(await store.TryImportProjectAsync("portable", async (target, ct) =>
        {
            await target.UpsertProjectAsync("portable", payload, 1, ct);
            await target.UpsertRecordAsync("portable", "inventory", "player", payload, false, 5, ct);
        }, CancellationToken.None));
        Assert.Single(store.Projects);
        Assert.Single(store.Records);
        Assert.False(await store.TryImportProjectAsync("portable",
            (_, _) => throw new InvalidOperationException("Existing IDs must not replay."), CancellationToken.None));
    }
}
