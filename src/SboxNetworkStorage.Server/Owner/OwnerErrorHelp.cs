namespace SboxNetworkStorage.Server.Owner;

/// <summary>One-line cause and fix for the HTTP statuses and error codes game developers hit most.</summary>
public static class OwnerErrorHelp
{
    public const string DocsUrl = OwnerDocs.Base + "client-setup.md#error-codes";

    private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal)
    {
        ["UNAUTHORIZED"] = "The API key is missing, wrong or disabled. Check the public key passed to NetworkStorage.Configure.",
        ["SBOX_AUTH_FAILED"] = "The project requires s&box authentication and the player's token was missing or did not verify. Run the game through s&box, or turn off Require s&box authentication for development.",
        ["ENDPOINT_ONLY"] = "The collection is not accessMode: public, so game clients cannot read or write it directly. Call it through an endpoint, or set accessMode: public.",
        ["RECORD_DELETE_DISABLED"] = "The collection does not allow deletes from game clients. Set allowRecordDelete: true, or delete through an endpoint.",
        ["FORBIDDEN"] = "The caller may not do this: a player wrote another player's record, or a secret key lacks the permission.",
        ["SAVE_NOT_CONFIRMED"] = "The client could not confirm a save reached the server. Check the player's connection and the request log around this time.",
        ["STALE_SAVE"] = "A save was based on older data than the stored record. The game should reload the record and retry.",
        ["SAVE_REGRESSION_BLOCKED"] = "A save would have lowered a protected value. Check the endpoint's write steps and the stored record.",
    };

    /// <summary>Help for an error code. Accepts prefixed classifications such as <c>EndpointNative.STALE_SAVE</c>.</summary>
    public static string? ForCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var dot = code.LastIndexOf('.');
        return Codes.GetValueOrDefault(dot < 0 ? code : code[(dot + 1)..]);
    }

    /// <summary>Help for a request-log status. Several codes share a status, so the line names each of them.</summary>
    public static string? ForStatus(int status) => status switch
    {
        400 => "The request was invalid. The response body names the field that failed.",
        401 => "UNAUTHORIZED or SBOX_AUTH_FAILED: the API key is missing, wrong or disabled, or the player's s&box token did not verify. Check the public key in NetworkStorage.Configure.",
        403 => "ENDPOINT_ONLY: the collection is not accessMode: public. RECORD_DELETE_DISABLED: allowRecordDelete is off. FORBIDDEN: a player wrote another player's record, or a secret key lacks the permission.",
        404 => "The project, collection, endpoint or record does not exist. Save or sync the definition and check the ID in the game code.",
        409 => "The save conflicted with newer stored data. The game should reload and retry.",
        413 => "The request body is larger than the server accepts.",
        429 => "A rate limit stopped this request.",
        >= 500 => "The server failed while handling this request. See the Errors tab, or run sbox-ns logs -f on the server.",
        _ => null,
    };
}
