using System.Collections.Generic;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

// Verifies the .NET port of the legacy checkPermission(keyData, scope, level)
// that gates the Network Storage record data plane.
public sealed class ApiKeyPermissionPolicyTests
{
    private static StorageApiKeyAuthResult Key(string keyType, Dictionary<string, string>? perms)
        => new(UserId: 1, ProjectId: "proj", Enabled: true, KeyType: keyType, Permissions: perms);

    [Fact]
    public void PublicKey_AlwaysHasCollectionData_RegardlessOfPermissions()
    {
        Assert.True(ApiKeyPermissionPolicy.CanAccessCollectionData(Key("public", null)));
        Assert.True(ApiKeyPermissionPolicy.CanAccessCollectionData(
            Key("public", new Dictionary<string, string> { ["collections"] = "none" })));
    }

    [Fact]
    public void SecretKey_NullPermissions_HasFullAccess()
    {
        Assert.True(ApiKeyPermissionPolicy.CanAccessCollectionData(Key("secret", null)));
    }

    [Fact]
    public void SecretKey_WithExecute_CanAccessCollectionData()
    {
        Assert.True(ApiKeyPermissionPolicy.CanAccessCollectionData(
            Key("secret", new Dictionary<string, string> { ["collections"] = "rwx" })));
        Assert.True(ApiKeyPermissionPolicy.CanAccessCollectionData(
            Key("secret", new Dictionary<string, string> { ["collections"] = "x" })));
    }

    [Fact]
    public void SecretKey_WithoutExecute_CannotAccessCollectionData()
    {
        Assert.False(ApiKeyPermissionPolicy.CanAccessCollectionData(
            Key("secret", new Dictionary<string, string> { ["collections"] = "rw" })));
        Assert.False(ApiKeyPermissionPolicy.CanAccessCollectionData(
            Key("secret", new Dictionary<string, string> { ["collections"] = "r" })));
    }

    [Fact]
    public void SecretKey_NoneOrMissingScope_Denied()
    {
        Assert.False(ApiKeyPermissionPolicy.CanAccessCollectionData(
            Key("secret", new Dictionary<string, string> { ["collections"] = "none" })));
        Assert.False(ApiKeyPermissionPolicy.CanAccessCollectionData(
            Key("secret", new Dictionary<string, string> { ["endpoints"] = "rwx" })));
        Assert.False(ApiKeyPermissionPolicy.CanAccessCollectionData(
            Key("secret", new Dictionary<string, string> { ["collections"] = "" })));
    }

    [Theory]
    [InlineData("r", "r", true)]
    [InlineData("r", "rw", true)]
    [InlineData("rw", "r", false)]
    [InlineData("rw", "rw", true)]
    [InlineData("rw", "rwx", true)]
    [InlineData("x", "rw", false)]
    [InlineData("x", "rwx", true)]
    public void HasPermission_LevelMatrix(string level, string granted, bool expected)
    {
        var auth = Key("secret", new Dictionary<string, string> { ["collections"] = granted });
        Assert.Equal(expected, ApiKeyPermissionPolicy.HasPermission(auth, "collections", level));
    }

    [Fact]
    public void NullAuth_IsAllowed()
    {
        Assert.True(ApiKeyPermissionPolicy.HasPermission(null, "collections", "x"));
    }
}
