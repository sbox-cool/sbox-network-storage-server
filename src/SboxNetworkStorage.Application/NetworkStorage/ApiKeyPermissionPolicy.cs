using System;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Application.NetworkStorage;

/// <summary>
/// Network Storage API-key permission policy. Faithful .NET port of the legacy
/// <c>checkPermission(keyData, scope, level)</c> in
/// <c>controllers/storage-shared.js</c>, so the .NET data plane enforces the
/// same access rules as the (retiring) Bun runtime.
///
/// Rules (in order):
/// <list type="bullet">
///   <item>Public keys are never permission-gated (always allowed).</item>
///   <item>A secret key with <c>null</c> permissions has full access.</item>
///   <item>Otherwise the requested <paramref name="scope"/> must be present,
///   not <c>"none"</c>, and grant the requested <paramref name="level"/>.</item>
/// </list>
///
/// Levels: <c>"r"</c> needs read; <c>"rw"</c> needs read AND write; <c>"x"</c>
/// needs execute. The direct collection-data HTTP API requires
/// <c>("collections", "x")</c> — execute = "direct collection data HTTP API
/// access" per the permission-scope docs.
/// </summary>
public static class ApiKeyPermissionPolicy
{
    public static bool HasPermission(StorageApiKeyAuthResult? auth, string scope, string level = "r")
    {
        if (auth is null) return true;
        if (!string.Equals(auth.KeyType, "secret", StringComparison.Ordinal)) return true;
        if (auth.Permissions is null) return true;

        if (!auth.Permissions.TryGetValue(scope, out var access)
            || string.IsNullOrEmpty(access)
            || string.Equals(access, "none", StringComparison.Ordinal))
        {
            return false;
        }

        var hasRead = access.Contains('r');
        var hasWrite = access.Contains('w');
        var hasExecute = access.Contains('x');

        return level switch
        {
            "x" => hasExecute,
            "rw" => hasRead && hasWrite,
            _ => hasRead,
        };
    }

    /// <summary>
    /// Authorizes the direct collection-data HTTP API (record read/write/delete):
    /// secret keys must hold <c>collections</c> execute; public keys are allowed.
    /// </summary>
    public static bool CanAccessCollectionData(StorageApiKeyAuthResult? auth)
        => HasPermission(auth, "collections", "x");
}
