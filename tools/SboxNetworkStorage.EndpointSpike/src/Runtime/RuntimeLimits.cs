namespace SboxNetworkStorage.EndpointSpike.Runtime;

/// <summary>Approved runtime caps from design.md. Enforced by the supervisor (process limits) and the broker (data limits).</summary>
public static class RuntimeLimits
{
    public const int MaxStaticExpressionOps = 10_000;
    public const int MaxSteps = 256;
    public const int MaxScannedRows = 1_000;
    public const long MaxInputBytes = 4L * 1024 * 1024;
    public const long MaxMaterializedBytes = 4L * 1024 * 1024;
    public const long MaxOutputBytes = 4L * 1024 * 1024;
    public const long WorkerCpuMs = 250;
    public const long WorkerWallMs = 1_000;
    public const long WorkerRssBytes = 128L * 1024 * 1024;
    /// <summary>Framing ceiling for one protocol line. Must hold a 4 MiB payload plus JSON escaping and envelope; not a payload budget.</summary>
    public const int MaxProtocolLineBytes = 16 * 1024 * 1024;

    public const string CodeCpu = "CPU_LIMIT";
    public const string CodeRss = "RSS_LIMIT";
    public const string CodeWall = "WALL_LIMIT";
    public const string CodeOutput = "OUTPUT_LIMIT";
    public const string CodeMaterialized = "MATERIALIZED_LIMIT";
    public const string CodeScan = "SCAN_LIMIT";
    public const string CodeInput = "INPUT_LIMIT";
}
