using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Hosting;

/// <summary>
/// The security-config signer (NetworkStorageSecurityConfigBuilder) reads its key and key id
/// from the process environment; this maps the server's secret file and
/// <c>auth.security_signing_key_id</c> onto those variables.
/// </summary>
public static class SecurityConfigEnvironment
{
    public const string PrivateKeyVariable = "NETWORK_STORAGE_SECURITY_CONFIG_PRIVATE_KEY";
    public const string KeyIdVariable = "NETWORK_STORAGE_SECURITY_CONFIG_KEY_ID";

    /// <summary>
    /// Sets the signing key and, when <c>auth.security_signing_key_id</c> is set, the key id.
    /// An empty setting leaves the key id as before (the variable if set, otherwise derived from the public key).
    /// </summary>
    public static void Apply(EffectiveConfig config, ServerSecretValues secrets)
    {
        Environment.SetEnvironmentVariable(PrivateKeyVariable, secrets.SecuritySigningKeyPem);
        var keyId = config.GetString("auth.security_signing_key_id").Trim();
        if (keyId.Length > 0)
        {
            Environment.SetEnvironmentVariable(KeyIdVariable, keyId);
        }
    }
}
