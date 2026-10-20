using System.Text.Json;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Security-mode decisions for an endpoint call, in the reference order
/// (controllers/endpoint-modules/execution-routes.js, services/network-storage-security-config.js).
/// A denial carries the <c>security</c> context the library uses to refresh its config and retry.
/// </summary>
internal static class EndpointSecurityGate
{
    private static readonly HashSet<string> ClientModes = new(StringComparer.Ordinal)
    {
        "legacy", "session", "encrypted", "session+encrypted",
    };

    private static readonly JsonSerializerOptions ProjectJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal readonly record struct Denial(int Status, string Code, string Message, object Security);

    /// <summary>The config version the signed config publishes for this project.</summary>
    public static string ConfigVersionOf(WorkspaceProject project)
        => NetworkStorageSecurityConfigVersion.Of(JsonSerializer.SerializeToElement(project, ProjectJson));

    /// <summary>The envelope kind of a request body (C3). <see cref="None"/> is a plain body.</summary>
    internal enum EnvelopeKind { None, V1, V2, Unsupported }

    /// <summary>
    /// The envelope kind of an encrypted body, from <c>envelope.version</c>: missing means "1", "2" is v2, any other
    /// value is unsupported.
    /// </summary>
    public static EnvelopeKind EnvelopeKindOf(JsonElement envelope)
    {
        var version = EncryptedEndpointEnvelope.Str(envelope, "version");
        return version is "" or "1" ? EnvelopeKind.V1 : version == "2" ? EnvelopeKind.V2 : EnvelopeKind.Unsupported;
    }

    /// <summary>
    /// The first security denial for this call, or null to continue. Runs after the client-mode, config-version, and
    /// session checks, in reference order. <paramref name="envelope"/>: the body's envelope kind.
    /// <paramref name="envelopeKeyFound"/>: v2 only; the envelope's keyId is a current or in-grace server key.
    /// </summary>
    public static Denial? Check(WorkspaceProject project, string configVersion, JsonElement body, bool hasSecretKey,
        bool presentedSession, EnvelopeKind envelope, bool envelopeKeyFound)
    {
        var encrypted = envelope != EnvelopeKind.None;
        var clientSecurity = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("security", out var security)
            && security.ValueKind == JsonValueKind.Object ? security : default;
        var clientMode = ReadTrimmed(clientSecurity, "clientMode");
        var clientConfigVersion = ReadTrimmed(clientSecurity, "configVersion");

        if (clientMode.Length > 0 && !ClientModes.Contains(clientMode))
            return Deny(project, configVersion, 400, "SECURITY_MODE_UNSUPPORTED",
                "The requested security mode is not supported.", clientMode);

        if (clientConfigVersion.Length > 0 && clientConfigVersion != configVersion)
            return Deny(project, configVersion, 409, "SECURITY_CONFIG_STALE",
                "The security config changed. Refresh the project config and retry.", clientMode);

        if (project.RequireAuthSessions == true && !presentedSession && !hasSecretKey)
            return Deny(project, configVersion, 401, "SECURITY_SESSION_REQUIRED",
                "This project requires an auth session for endpoint calls.",
                encrypted ? "encrypted" : "legacy");

        if (presentedSession && !hasSecretKey && project.EnableAuthSessions != true)
            return Deny(project, configVersion, 403, "SECURITY_SESSION_DISABLED",
                "Auth sessions are not enabled for this project.",
                encrypted ? "session+encrypted" : "session");

        // C3 denial matrix: the envelope kind against the resolved project mode.
        var mode = project.EncryptedRequestMode;
        var encryptedMode = presentedSession ? "session+encrypted" : "encrypted";
        switch (envelope)
        {
            case EnvelopeKind.None when !EncryptedRequestModes.AcceptsPlain(mode):
                return Deny(project, configVersion, 409, "SECURITY_ENCRYPTION_REQUIRED",
                    "This project requires encrypted endpoint requests.", presentedSession ? "session" : "legacy");
            case EnvelopeKind.V1 when !EncryptedRequestModes.AcceptsV1(mode):
                return mode == EncryptedRequestModes.Off
                    ? Deny(project, configVersion, 409, "SECURITY_ENCRYPTION_DISABLED",
                        "Encrypted requests are not enabled for this project.", encryptedMode)
                    : Deny(project, configVersion, 409, "SECURITY_ENCRYPTION_REQUIRED",
                        "This project requires v2 encrypted endpoint requests.", encryptedMode);
            case EnvelopeKind.V2 when !EncryptedRequestModes.AcceptsV2(mode):
                return mode == EncryptedRequestModes.Off
                    ? Deny(project, configVersion, 409, "SECURITY_ENCRYPTION_DISABLED",
                        "Encrypted requests are not enabled for this project.", encryptedMode)
                    : Deny(project, configVersion, 400, "SECURITY_MODE_UNSUPPORTED",
                        "This project does not accept v2 encrypted requests.", encryptedMode);
            case EnvelopeKind.Unsupported:
                return Deny(project, configVersion, 400, "SECURITY_MODE_UNSUPPORTED",
                    "The encrypted request envelope version is not supported.", encryptedMode);
        }

        // A v2 envelope names the server key it was wrapped to. An unknown key, or a retired one past its grace, is stale.
        if (envelope == EnvelopeKind.V2 && !envelopeKeyFound)
            return Deny(project, configVersion, 409, "SECURITY_CONFIG_STALE",
                "The encryption key is unknown or expired. Refresh the project config and retry.", encryptedMode);

        return null;
    }

    private static Denial Deny(WorkspaceProject project, string configVersion, int status, string code,
        string message, string requestMode)
        => new(status, code, message, new
        {
            code,
            retrySafe = true,
            configVersion,
            configSource = "project",
            requestMode,
            expected = new
            {
                authSessions = project.EnableAuthSessions == true ? "enabled" : "disabled",
                encryptedRequests = EncryptedRequestModes.RequiresEncryption(project.EncryptedRequestMode) ? "required" : "disabled",
                encryptionMode = project.EncryptedRequestMode,
            },
        });

    private static string ReadTrimmed(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";
}
