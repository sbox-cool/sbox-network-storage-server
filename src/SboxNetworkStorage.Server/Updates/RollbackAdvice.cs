namespace SboxNetworkStorage.Server.Updates;

/// <summary>
/// The rollback steps every post-update regression surface gives the owner (alert, owner console, doctor,
/// <c>update verify</c>), so they never disagree.
/// </summary>
public static class RollbackAdvice
{
    public const string KeepData = "sbox-ns rollback --keep-data";
    public const string RestoreBackup = "sbox-ns rollback";
    public const string Image = "ghcr.io/sbox-cool/sbox-network-storage-server";

    /// <summary>One line per way back, most data-preserving first.</summary>
    public static IReadOnlyList<string> Steps(string? previousVersion) =>
    [
        $"`{KeepData}`: previous binary, database kept as it is (refused when a migration since the update is not rollback-safe).",
        $"`{RestoreBackup}`: previous binary plus the database backup taken before the update; writes since the update are lost.",
        $"Docker: recreate the container from `{Image}:{previousVersion ?? "<previous version>"}` with the same volumes.",
    ];

    public static string Text(string? previousVersion) =>
        "Roll back with " + string.Join(" Or ", Steps(previousVersion)) + " Linux service installs need sudo.";
}
