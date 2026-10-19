namespace SboxNetworkStorage.Domain.Workspace;

/// <summary>
/// Per-project encrypted-request modes. The stored <c>encryptedRequestMode</c> wins when valid. When it is absent, the
/// legacy <c>enableEncryptedRequests</c> flag maps to <see cref="V1"/> (true) or <see cref="Off"/>.
/// </summary>
public static class EncryptedRequestModes
{
    public const string Off = "off";
    public const string V1 = "v1";
    public const string V2Preferred = "v2-preferred";
    public const string V2Required = "v2-required";

    public static bool IsValid(string? mode) => mode is Off or V1 or V2Preferred or V2Required;

    public static string Resolve(string? storedMode, bool? legacyEnableEncryptedRequests)
        => IsValid(storedMode) ? storedMode! : legacyEnableEncryptedRequests == true ? V1 : Off;

    /// <summary>Plain (unencrypted) calls pass only when the project is off.</summary>
    public static bool AcceptsPlain(string mode) => mode == Off;

    public static bool AcceptsV1(string mode) => mode is V1 or V2Preferred;

    public static bool AcceptsV2(string mode) => mode is V2Preferred or V2Required;

    /// <summary>True when the signed config publishes the v2 <c>encryption</c> block.</summary>
    public static bool OffersV2(string mode) => mode is V2Preferred or V2Required;

    public static bool RequiresEncryption(string mode) => mode != Off;
}
