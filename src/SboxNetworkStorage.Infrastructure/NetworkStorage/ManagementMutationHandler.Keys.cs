using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

public sealed partial class ManagementMutationHandler
{
    // ── DELETE /keys ──

    /// <summary>
    /// <c>DELETE /v3/manage/:projectId/keys</c>: revokes the key pair named by <c>x-api-key</c> (secret) and
    /// <c>x-public-key</c> (public). Port of the reference <c>routeManageDestroyKeys</c> and
    /// <c>destroyApiKeyPair</c>. Success is reported only after both keys stop resolving.
    /// </summary>
    private async Task<NetworkStorageResult> DestroyKeyPairAsync(NetworkStorageRequest request, string projectId, long ownerUserId)
    {
        var secretKey = request.Credentials.ApiKey ?? string.Empty;
        var publicKey = request.PublicKey ?? string.Empty;
        if (!secretKey.StartsWith("sbox_sk_", StringComparison.Ordinal))
            return ManagementRouteFailure(400, "INVALID_SECRET_KEY", "x-api-key must be a secret key (sbox_sk_*).");
        if (!publicKey.StartsWith("sbox_ns_", StringComparison.Ordinal))
            return ManagementRouteFailure(400, "INVALID_PUBLIC_KEY", "x-public-key must be a public key (sbox_ns_*).");
        if (_keyRevocation is null)
            return ManagementRouteFailure(503, "KEY_REVOCATION_UNAVAILABLE", "Key revocation service is not available.");

        var ct = request.CancellationToken;
        var publicAuth = await _apiKeyResolver.ResolveApiKeyAsync(publicKey, projectId, ct);
        if (publicAuth is not { KeyType: "public" })
            return ManagementRouteFailure(401, "INVALID_PUBLIC_KEY", "Invalid public key for this project.");

        // The store keeps only the masked display value of a secret key (api_key) plus its hash; the project
        // service removes rows by api_key, so resolve the row from the hash of the raw key.
        var secretHash = StorageKeyCrypto.HashSecretKey(secretKey);
        var secretRow = (await _store.ListApiKeysAsync(projectId, ct)).FirstOrDefault(row =>
            string.Equals(KeyRowString(row, "key_type"), "secret", StringComparison.Ordinal)
            && string.Equals(KeyRowString(row, "key_hash"), secretHash, StringComparison.OrdinalIgnoreCase));
        var secretStoredKey = secretRow.ValueKind == JsonValueKind.Undefined ? null : KeyRowString(secretRow, "api_key");
        if (string.IsNullOrEmpty(secretStoredKey))
            return ManagementRouteFailure(401, "INVALID_SECRET_KEY", "Invalid secret key.");

        // Public key first: the secret authenticates this request, so if the public removal fails the caller can retry.
        await _keyRevocation.RemoveProjectKeyAsync(ownerUserId, projectId, publicKey, ct);
        await _keyRevocation.RemoveProjectKeyAsync(ownerUserId, projectId, secretStoredKey, ct);

        // Never report success unless both keys stop resolving (the project service invalidates the key cache).
        if (await _apiKeyResolver.ResolveApiKeyAsync(publicKey, projectId, ct) is not null
            || await _apiKeyResolver.ResolveApiKeyAsync(secretKey, projectId, ct) is not null)
        {
            return ManagementRouteFailure(500, "KEY_REVOCATION_INCOMPLETE",
                "The key pair is still active after revocation; nothing was destroyed.");
        }

        return ProductionOkResult(new { ok = true, message = "Key pair destroyed." });
    }

    internal static NetworkStorageResult ManagementRouteFailure(int status, string code, string message) =>
        NetworkStorageResult.Error(status, code,
            new { ok = false, error = code, message },
            authDecision: "allowed");

    private static string? KeyRowString(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
