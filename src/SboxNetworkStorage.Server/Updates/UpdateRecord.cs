using System.Text.Json;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>
/// <c>&lt;data dir&gt;/updates/last-update.json</c>: the last install into this instance.
/// <c>Status</c> is <c>succeeded</c> (rollback possible) or <c>failed</c> (automatic recovery
/// attempted; <c>Reason</c> identifies incomplete recovery). Older records read as succeeded.
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

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public bool IsFailed => Status == Failed;

    public static string PathFor(EffectiveConfig config) => System.IO.Path.Combine(config.DataDirectory, "updates", "last-update.json");

    public static UpdateRecord? Read(EffectiveConfig config)
    {
        var path = PathFor(config);
        return File.Exists(path) ? JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(path)) : null;
    }

    public static void Write(EffectiveConfig config, UpdateRecord record)
        => ConfigFiles.WriteAtomically(PathFor(config), JsonSerializer.Serialize(record, WriteOptions));

    public static void Delete(EffectiveConfig config) => File.Delete(PathFor(config));
}
