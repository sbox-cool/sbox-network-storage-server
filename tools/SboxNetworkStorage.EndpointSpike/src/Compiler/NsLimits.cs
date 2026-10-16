namespace SboxNetworkStorage.EndpointSpike.Compiler;

/// <summary>Approved spike caps from design.md (Resource and ambient-I/O boundary). Compile-time caps only; runtime caps live in Runtime/RuntimeLimits.cs.</summary>
public static class NsLimits
{
    public const int MaxSourceBytes = 64 * 1024;
    public const int MaxNestingDepth = 64;
    public const int MaxAstNodes = 4096;
    public const int MaxLoweredSteps = 256;
    public const int MaxPlanBytes = 1024 * 1024;
    /// <summary>Static charge for expression operations, estimated at compile time from the lowered plan.</summary>
    public const int MaxStaticExpressionOps = 10_000;
}
