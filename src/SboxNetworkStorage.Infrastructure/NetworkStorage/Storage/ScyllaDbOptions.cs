namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

/// <summary>
/// Connection and behavior settings for the production ScyllaDB integration,
/// bound from the <c>Scylla</c> configuration section. Extends the prototype
/// options with shadow mode, fleet discovery, snapshot backup, and per-resource
/// consistency profile settings.
/// </summary>
public sealed class ScyllaDbOptions
{
    public const string SectionName = "Scylla";

    /// <summary>
    /// Static contact point overrides. When empty, contact points are discovered
    /// from the fleet roster (<c>vps_nodes</c> table) via
    /// <c>IAdminRepository.GetApiWorkersAsync()</c>.
    /// </summary>
    public string[] ContactPoints { get; set; } = [];

    /// <summary>CQL native transport port.</summary>
    public int Port { get; set; } = 9042;

    /// <summary>Keyspace to connect to.</summary>
    public string Keyspace { get; set; } = "sboxcool";

    /// <summary>
    /// Keyspace holding the usage counter tables (<c>project_usage_*</c>).
    /// Counters are not supported in tablets-enabled keyspaces (ScyllaDB 6.x
    /// enables tablets by default for NetworkTopologyStrategy), so the counters
    /// live in a companion keyspace created with tablets disabled and the main
    /// keyspace's replication. Defaults to <c>{Keyspace}_usage</c>.
    /// </summary>
    public string? UsageKeyspace { get; set; }

    /// <summary>Resolved usage counter keyspace (see <see cref="UsageKeyspace"/>).</summary>
    public string EffectiveUsageKeyspace =>
        string.IsNullOrWhiteSpace(UsageKeyspace) ? Keyspace + "_usage" : UsageKeyspace.Trim();

    /// <summary>Default write consistency level for all resource types.</summary>
    public string WriteConsistency { get; set; } = "Quorum";

    /// <summary>Default read consistency level for all resource types.</summary>
    public string ReadConsistency { get; set; } = "Quorum";

    /// <summary>Maximum accepted JSON payload size, in UTF-8 bytes.</summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Maximum number of in-flight prepared-statement writes during a project
    /// import. Bulk imports dispatch row writes with this much concurrency rather
    /// than one awaited round-trip at a time, turning O(rows) serial latency into
    /// O(rows / concurrency). Clamped to [1, 1024] by the executor.
    /// </summary>
    public int MaxImportConcurrency { get; set; } = 128;

    /// <summary>Socket connect timeout, in milliseconds.</summary>
    public int ConnectTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Interval in seconds between fleet roster refreshes for contact point
    /// discovery. Set to 0 to disable periodic refresh (startup only).
    /// </summary>
    public int FleetRefreshIntervalSeconds { get; set; } = 60;

    // ── Cutover ──

    /// <summary>
    /// When true, ScyllaDB is the authoritative Network Storage data plane: the
    /// .NET backend reads and writes records through ScyllaDB only — there is no
    /// Bunny or SpacetimeDB data fallback (Bunny is used solely for snapshot
    /// backups). Writes are ScyllaDB-authoritative (fail-closed). When false,
    /// the pre-cutover Bunny path is used unchanged.
    /// </summary>
    public bool Primary { get; set; }

    /// <summary>
    /// DEPRECATED: the data plane is now ScyllaDB-only and no longer reads this
    /// flag. Retained for backward compatibility with existing
    /// <c>Scylla__Authoritative</c> env vars. Has no effect on read behavior.
    /// </summary>
    public bool Authoritative { get; set; } = true;

    // ── Shadow Mode ──

    /// <summary>Whether shadow comparison against the authoritative upstream is enabled.</summary>
    public bool ShadowEnabled { get; set; }

    /// <summary>
    /// Steam IDs whose requests are shadowed. When empty and
    /// <see cref="ShadowEnabled"/> is true, all requests are shadowed.
    /// </summary>
    public string[] ShadowSteamIds { get; set; } = [];

    /// <summary>Shadow comparison timeout, in milliseconds.</summary>
    public int ShadowTimeoutMs { get; set; } = 2000;

    /// <summary>Maximum shadow comparison results retained in memory.</summary>
    public int ShadowMaxResults { get; set; } = 10_000;

    /// <summary>Shadow result retention period in days.</summary>
    public int ShadowRetentionDays { get; set; } = 30;

    // ── Snapshot Backup ──

    /// <summary>Whether scheduled snapshot backups are enabled.</summary>

    /// <summary>
    /// Docker container name for the local ScyllaDB node. Used by the snapshot
    /// backup service to run <c>nodetool snapshot</c> and <c>docker cp</c>.
    /// Defaults to <c>scylla-node1</c>. Override via <c>Scylla__SnapshotContainer</c>
    /// when the local node hosts a differently-named container (e.g.
    /// <c>scylla-node2</c>).
    /// </summary>
    public string SnapshotContainer { get; set; } = "scylla-node1";
    public bool SnapshotEnabled { get; set; }

    /// <summary>Snapshot backup interval in hours.</summary>
    public int SnapshotIntervalHours { get; set; } = 6;

    /// <summary>Bunny CDN fast-edge bucket path prefix for backups.</summary>
    public string SnapshotCdnPrefix { get; set; } = "scylla-backups";

    /// <summary>Backup retention period in days.</summary>
    public int SnapshotRetentionDays { get; set; } = 30;

    // ── Project Backup Scheduler ──

    /// <summary>Whether the scheduled per-project export backup worker is enabled.</summary>
    public bool ProjectBackupSchedulerEnabled { get; set; } = true;

    /// <summary>How frequently the scheduler wakes up to check for due backups, in minutes.</summary>
    public int ProjectBackupSchedulerIntervalMinutes { get; set; } = 10;

    /// <summary>Delay before the first scheduler pass after host startup, in minutes.</summary>
    public int ProjectBackupSchedulerInitialDelayMinutes { get; set; } = 2;

    /// <summary>
    /// Fallback replication factor used by node-health checks when the keyspace
    /// replication options cannot be read from cluster metadata. Tracks the
    /// production target (RF ≥ 3, NetworkTopologyStrategy); only a safety net.
    /// </summary>
    public int DefaultReplicationFactor { get; set; } = 3;

    // ── Keyspace durability (enforced in code, not a manual runbook step) ──

    /// <summary>
    /// Minimum acceptable replication factor for the Network Storage keyspace.
    /// The keyspace is created at <c>max(ReplicationFloor, DefaultReplicationFactor)</c>,
    /// and startup verification fails (when <see cref="RequireDurableKeyspace"/>)
    /// if the live keyspace replicates below this floor. RF=3 survives a single
    /// node loss at QUORUM; RF&lt;3 is the instability we are eliminating.
    /// </summary>
    public int ReplicationFloor { get; set; } = 3;

    /// <summary>
    /// Replication strategy for the keyspace. Must be a topology-aware strategy
    /// (<c>NetworkTopologyStrategy</c>) in production; <c>SimpleStrategy</c> is
    /// rejected by durability verification because it does not place replicas
    /// across the fleet safely.
    /// </summary>
    public string ReplicationStrategy { get; set; } = "NetworkTopologyStrategy";

    /// <summary>
    /// Whether the keyspace is created with <c>durable_writes = true</c> (writes
    /// hit the commitlog before ack). Disabling this can lose acknowledged writes
    /// on a node crash — the exact failure class this change exists to prevent.
    /// </summary>
    public bool DurableWrites { get; set; } = true;

    /// <summary>
    /// When true, startup keyspace verification throws if the live keyspace is
    /// SimpleStrategy, has durable_writes off, or replicates below
    /// <see cref="ReplicationFloor"/> — refusing to serve at-risk data. Default
    /// is false (warn only) so a host whose ScyllaDB is shadow-only is not taken
    /// down by a keyspace still being raised to the floor. The cutover config
    /// sets this true together with <see cref="Primary"/>, after RF has been
    /// raised to the floor and repaired, so durability is enforced exactly when
    /// ScyllaDB holds authoritative data. Set false for a single-node dev box.
    /// </summary>
    public bool RequireDurableKeyspace { get; set; }

    // ── Per-Resource Consistency Overrides ──

    /// <summary>
    /// Per-resource read consistency overrides. Keys are resource type names
    /// (e.g., "records", "project_audit_logs"). API-key validation defaults to
    /// <c>LocalOne</c> so previously committed keys remain readable during an
    /// RF=2 single-node outage; all other resources fall back to
    /// <see cref="ReadConsistency"/> when not listed.
    /// </summary>
    public Dictionary<string, string> ReadConsistencyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api_keys"] = "LocalOne",
    };

    /// <summary>
    /// Per-resource write consistency overrides. Keys are resource type names.
    /// Falls back to <see cref="WriteConsistency"/> when a resource is not listed.
    /// </summary>
    public Dictionary<string, string> WriteConsistencyOverrides { get; set; } = [];
}
