namespace SboxNetworkStorage.Server.Owner;

/// <summary>Links from the dashboard to the repository documentation.</summary>
public static class OwnerDocs
{
    public const string Base = "https://github.com/sbox-cool/sbox-network-storage-server/blob/main/docs/";

    /// <summary>A page under <c>docs/</c>, optionally with an anchor, for example <c>client-setup.md#error-codes</c>.</summary>
    public static string Url(string page) => Base + page;
}
