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

    /// <summary>
    /// The first security denial for this call, or null to continue.
    /// <paramref name="presentedSession"/>: an auth-session token is present (not yet validated).
    /// <paramref name="encryptedEnvelope"/>: the body is an encrypted envelope.
    /// </summary>
    public static Denial? Check(WorkspaceProject project, string configVersion, JsonElement body, bool hasSecretKey,
        bool presentedSession, bool encryptedEnvelope)
    {
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
                encryptedEnvelope ? "encrypted" : "legacy");

        if (presentedSession && !hasSecretKey && project.EnableAuthSessions != true)
            return Deny(project, configVersion, 403, "SECURITY_SESSION_DISABLED",
                "Auth sessions are not enabled for this project.",
                encryptedEnvelope ? "session+encrypted" : "session");

        if (project.EnableEncryptedRequests != true && encryptedEnvelope)
            return Deny(project, configVersion, 409, "SECURITY_ENCRYPTION_DISABLED",
                "Encrypted requests are not enabled for this project.",
                presentedSession ? "session+encrypted" : "encrypted");

        if (project.EnableEncryptedRequests == true && !encryptedEnvelope)
            return Deny(project, configVersion, 409, "SECURITY_ENCRYPTION_REQUIRED",
                "This project requires encrypted endpoint requests.",
                presentedSession ? "session" : "legacy");

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
                encryptedRequests = project.EnableEncryptedRequests == true ? "required" : "disabled",
            },
        });

    private static string ReadTrimmed(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";
}
