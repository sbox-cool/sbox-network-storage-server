using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Project settings as the management API reads and writes them. Port of the reference
/// <c>projectSettingsForManage</c> and <c>applyProjectSettingsUpdate</c> (<c>controllers/manage-api-controller.js</c>
/// L347-468), with the clamp rules from <c>services/network-storage-auth-sessions.js</c> and
/// <c>services/network-storage-encrypted-requests.js</c>. Used by both the GET and PUT settings routes so they agree.
/// </summary>
internal static class ManagementSettings
{
    private const int DefaultAuthSessionTtlSeconds = 3600;
    private const int MinAuthSessionTtlSeconds = 60;
    private const int MaxAuthSessionTtlSeconds = 86_400;
    private const int DefaultEncryptedRequestWindowSeconds = 120;
    private const int MinEncryptedRequestWindowSeconds = 30;
    private const int MaxEncryptedRequestWindowSeconds = 600;
    private const int MaxRevisionNotifyMessageLength = 500;

    private static readonly Regex LeadingInteger = new(@"^\s*[+-]?\d+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Settings the reference accepts but this server has no field to store. They are rejected with a typed error
    /// rather than acknowledged, so a caller never believes a value was saved when it was not.
    /// </summary>
    private static readonly string[] UnstoredKeys = ["spacetimeBackendMode", "storageBackendMode"];

    public static string? FirstUnstoredKey(JsonElement body) =>
        UnstoredKeys.FirstOrDefault(key => body.TryGetProperty(key, out _));

    public static Dictionary<string, object?> View(WorkspaceProject project)
    {
        var settings = new Dictionary<string, object?>
        {
            ["enabled"] = project.Enabled,
            ["requireSboxAuth"] = project.RequireSboxAuth == true,
            ["requireAuthSessions"] = project.RequireAuthSessions == true,
            ["enableAuthSessions"] = project.EnableAuthSessions == true,
            ["authSessionTtlSeconds"] = project.AuthSessionTtlSeconds ?? DefaultAuthSessionTtlSeconds,
            ["enableEncryptedRequests"] = project.EnableEncryptedRequests == true,
            ["encryptedRequestWindowSeconds"] = project.EncryptedRequestWindowSeconds ?? 300,
            ["playerKeyMode"] = project.PlayerKeyMode ?? "player",
            ["revisionEnforcementEnabled"] = project.RevisionEnforcementEnabled == true,
            ["revisionEnforcementMode"] = project.RevisionEnforcementMode ?? "allow_continue",
            ["revisionGracePeriodMinutes"] = (object?)project.RevisionGracePeriodMinutes,
            ["revisionPostGraceAction"] = project.RevisionPostGraceAction ?? "block_writes",
            ["revisionForceEndpointUpgrade"] = project.RevisionForceEndpointUpgrade == true,
            ["revisionNotifyMessage"] = project.RevisionNotifyMessage ?? "",
            ["revisionShowDefaultMessage"] = project.RevisionShowDefaultMessage == true,
            ["revisionShowNewVersionBanner"] = project.RevisionShowNewVersionBanner != false,
            ["revisionShowUpdateOptions"] = project.RevisionShowUpdateOptions != false,
            ["revisionEscalateAfterMinutes"] = (object?)project.RevisionEscalateAfterMinutes,
            ["revisionShowPopupOnce"] = project.RevisionShowPopupOnce != false,
            ["revisionTestOutdatedEditor"] = project.RevisionTestOutdatedEditor == true,
            ["revisionTestOutdatedLive"] = project.RevisionTestOutdatedLive == true,
        };
        // Version rules (game-version-policy) are additive: the keys appear only once an owner sets them, so a
        // project that never used them returns exactly the body older libraries and the parity corpus expect.
        if (project.RevisionMinimumRevisionId is { } minimumRevision) settings["revisionMinimumRevisionId"] = minimumRevision;
        if (project.RevisionInGraceAction is { } inGraceAction) settings["revisionInGraceAction"] = inGraceAction;
        if (project.RevisionServerEnforcementEnabled == true) settings["revisionServerEnforcementEnabled"] = true;
        if (project.RevisionRetentionCount is { } retention) settings["revisionRetentionCount"] = retention;
        return settings;
    }

    /// <summary>Applies the keys present in <paramref name="body"/>, mirroring the reference's <c>!== undefined</c> checks.</summary>
    public static WorkspaceProject Apply(WorkspaceProject project, JsonElement body)
    {
        var updated = project;

        if (Field(body, "enabled") is { } enabledValue) updated = updated with { Enabled = IsTruthy(enabledValue) };
        if (Field(body, "requireSboxAuth") is { } requireAuthValue) updated = updated with { RequireSboxAuth = IsTruthy(requireAuthValue) };
        if (Field(body, "requireAuthSessions") is { } requireAuthSessions) updated = updated with { RequireAuthSessions = IsTruthy(requireAuthSessions) };
        if (Field(body, "enableAuthSessions") is { } authSessionsValue) updated = updated with { EnableAuthSessions = IsTruthy(authSessionsValue) };
        if (Field(body, "authSessionTtlSeconds") is { } ttlValue)
            updated = updated with { AuthSessionTtlSeconds = ClampInt(ParseIntLike(ttlValue), DefaultAuthSessionTtlSeconds, MinAuthSessionTtlSeconds, MaxAuthSessionTtlSeconds) };
        if (Field(body, "enableEncryptedRequests") is { } encryptedValue) updated = updated with { EnableEncryptedRequests = IsTruthy(encryptedValue) };
        if (Field(body, "encryptedRequestWindowSeconds") is { } windowValue)
            updated = updated with { EncryptedRequestWindowSeconds = ClampInt(ParseIntLike(windowValue), DefaultEncryptedRequestWindowSeconds, MinEncryptedRequestWindowSeconds, MaxEncryptedRequestWindowSeconds) };
        if (Field(body, "playerKeyMode") is { } keyModeValue && IsOneOf(keyModeValue, "player", "playerSave"))
            updated = updated with { PlayerKeyMode = keyModeValue.GetString() };

        if (Field(body, "revisionEnforcementEnabled") is { } enforcementEnabled)
            updated = updated with { RevisionEnforcementEnabled = IsTruthy(enforcementEnabled) };
        if (Field(body, "revisionEnforcementMode") is { } enforcementMode && IsOneOf(enforcementMode, "force_upgrade", "allow_continue"))
            updated = updated with { RevisionEnforcementMode = enforcementMode.GetString() };
        if (Field(body, "revisionGracePeriodMinutes") is { } graceValue)
            updated = updated with { RevisionGracePeriodMinutes = PositiveMinutes(JsNumber(graceValue), 5, 1440) };
        // Legacy alias: allow_readonly maps to allow_continue with block_writes, as the reference does.
        if (Field(body, "revisionPostGraceAction") is { } postGraceValue)
        {
            if (IsOneOf(postGraceValue, "allow_readonly"))
                updated = updated with { RevisionEnforcementMode = "allow_continue", RevisionPostGraceAction = "block_writes" };
            else if (IsOneOf(postGraceValue, "block_writes", "block_all"))
                updated = updated with { RevisionPostGraceAction = postGraceValue.GetString() };
        }
        if (Field(body, "revisionForceEndpointUpgrade") is { } forceUpgrade)
            updated = updated with { RevisionForceEndpointUpgrade = IsTruthy(forceUpgrade) };
        if (Field(body, "revisionNotifyMessage") is { } notifyValue)
            updated = updated with { RevisionNotifyMessage = Truncate(JsString(notifyValue), MaxRevisionNotifyMessageLength) };
        if (Field(body, "revisionShowDefaultMessage") is { } showDefault)
            updated = updated with { RevisionShowDefaultMessage = IsTruthy(showDefault) };
        if (Field(body, "revisionShowNewVersionBanner") is { } showBanner)
            updated = updated with { RevisionShowNewVersionBanner = IsTruthy(showBanner) };
        if (Field(body, "revisionShowUpdateOptions") is { } showOptions)
            updated = updated with { RevisionShowUpdateOptions = IsTruthy(showOptions) };
        if (Field(body, "revisionEscalateAfterMinutes") is { } escalateValue)
            updated = updated with { RevisionEscalateAfterMinutes = PositiveMinutes(JsNumber(escalateValue), 60, 1440) };
        if (Field(body, "revisionShowPopupOnce") is { } popupOnce)
            updated = updated with { RevisionShowPopupOnce = IsTruthy(popupOnce) };

        // The reference normalises these on every save, so stored out-of-range values are corrected too.
        return updated with
        {
            EnableAuthSessions = updated.EnableAuthSessions == true,
            AuthSessionTtlSeconds = ClampInt(updated.AuthSessionTtlSeconds, DefaultAuthSessionTtlSeconds, MinAuthSessionTtlSeconds, MaxAuthSessionTtlSeconds),
            EnableEncryptedRequests = updated.EnableEncryptedRequests == true,
            EncryptedRequestWindowSeconds = ClampInt(updated.EncryptedRequestWindowSeconds, DefaultEncryptedRequestWindowSeconds, MinEncryptedRequestWindowSeconds, MaxEncryptedRequestWindowSeconds),
            PlayerKeyMode = updated.PlayerKeyMode ?? "player",
        };
    }

    private static JsonElement? Field(JsonElement body, string key) =>
        body.TryGetProperty(key, out var value) ? value : null;

    private static bool IsOneOf(JsonElement value, params string[] options) =>
        value.ValueKind == JsonValueKind.String && options.Contains(value.GetString(), StringComparer.Ordinal);

    // Port of the reference isTruthyFlag: true, 1, or a string "true" | "on" | "yes" | "1" (case-insensitive).
    private static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetDouble(out var number) && number == 1,
        JsonValueKind.String => value.GetString() is { } text && text.ToLowerInvariant() is "true" or "on" or "yes" or "1",
        _ => false,
    };

    // Port of JavaScript Number(value): null/false -> 0, true -> 1, blank string -> 0, otherwise NaN when unparseable.
    private static double JsNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False or JsonValueKind.Null => 0,
        JsonValueKind.String => value.GetString()?.Trim() is { Length: 0 } or null
            ? 0
            : double.TryParse(value.GetString()!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : double.NaN,
        _ => double.NaN,
    };

    // Port of the reference `Number(val) > 0 ? clamp(val, min, max) : null`.
    private static int? PositiveMinutes(double minutes, int min, int max) =>
        minutes > 0 ? (int)Math.Clamp(Math.Truncate(minutes), min, max) : null;

    // Port of JavaScript parseInt(String(value)): the leading integer of the text, or null when there is none.
    private static int? ParseIntLike(JsonElement value)
    {
        double? parsed = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => LeadingInteger.Match(value.GetString() ?? "") is { Success: true } match
                && double.TryParse(match.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var text)
                    ? text
                    : null,
            _ => null,
        };
        if (parsed is not { } number || !double.IsFinite(number)) return null;
        return (int)Math.Clamp(Math.Truncate(number), int.MinValue, int.MaxValue);
    }

    private static int ClampInt(int? value, int fallback, int min, int max) =>
        value is { } number ? Math.Clamp(number, min, max) : fallback;

    // Port of JavaScript String(value || ""): falsy values become "", strings pass through, scalars use their text.
    private static string JsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.Number => value.GetRawText(),
        _ => "",
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;
}
