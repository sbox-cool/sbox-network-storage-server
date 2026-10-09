namespace SboxNetworkStorage.Server.Updates;

/// <summary>
/// <c>&lt;update state&gt;/&lt;instance&gt;/last-update.json</c> (see <see cref="UpdateState"/>): the last
/// install into one instance. <c>Status</c> is <c>succeeded</c> (rollback possible) or <c>failed</c>
/// (automatic recovery attempted; <c>Reason</c> identifies incomplete recovery). The binary paths are
/// informational: rollback only ever restores the fixed installed binary.
/// </summary>
public sealed record UpdateRecord(
    string FromVersion,
    string ToVersion,
    string BinaryPath,
    string PreviousBinaryPath,
    string? BackupPath,
    DateTimeOffset UpdatedAt,
    string? Status = UpdateRecord.Succeeded,
    string? Reason = null,
    string? Mode = null)
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public bool IsFailed => Status == Failed;
}
