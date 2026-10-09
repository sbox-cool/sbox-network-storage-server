using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.Workspace;

public sealed class ProjectActivitySupportTests
{
    [Fact]
    public void Compute_ReturnsZero_WhenNoTimestamps()
    {
        var project = new WorkspaceProject("p1", "Test", null, true, null, null, null);
        var snapshot = ProjectActivitySupport.Compute(project, "[]", "[]", "[]", null);

        Assert.Equal(0, snapshot.LastActivityMs);
        Assert.Null(snapshot.LastActivityAt);
    }

    [Fact]
    public void Compute_UsesProjectCreatedAt_WhenNoChildren()
    {
        var createdAt = DateTimeOffset.UtcNow.AddDays(-5);
        var project = new WorkspaceProject("p1", "Test", null, true, createdAt, null, null);
        var snapshot = ProjectActivitySupport.Compute(project, "[]", "[]", "[]", null);

        Assert.Equal(createdAt.ToUnixTimeMilliseconds(), snapshot.LastActivityMs);
        Assert.NotNull(snapshot.LastActivityAt);
    }

    [Fact]
    public void Compute_PrefersNewestTimestampAcrossSources()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        var recent = DateTimeOffset.UtcNow.AddDays(-2);
        var newest = DateTimeOffset.UtcNow.AddMinutes(-3);

        var project = new WorkspaceProject("p1", "Test", null, true, old, null, null);
        var collections = $"[{{\"createdAt\":\"{recent:o}\",\"updatedAt\":\"{old:o}\"}}]";
        var endpoints = $"[{{\"updatedAt\":\"{newest:o}\"}}]";

        var snapshot = ProjectActivitySupport.Compute(project, collections, endpoints, "[]", null);

        Assert.Equal(newest.ToUnixTimeMilliseconds(), snapshot.LastActivityMs);
        Assert.Equal(1, snapshot.CollectionCount);
        Assert.Equal(1, snapshot.EndpointCount);
    }

    [Fact]
    public void Compute_UsesUsagePerDayTimestamps()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var project = new WorkspaceProject("p1", "Test", null, true, null, null, null);
        var usage = $"{{\"perDay\":{{\"{today}\":{{\"requests\":1}}}}}}";

        var snapshot = ProjectActivitySupport.Compute(project, "[]", "[]", "[]", usage);

        Assert.True(snapshot.LastActivityMs > 0);
    }
}
