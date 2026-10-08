namespace SboxNetworkStorage.Contracts.NetworkStorage;

public sealed record NetworkStorageShadowRequest(
    string Method,
    string PathAndQuery,
    bool Mutating,
    IReadOnlyDictionary<string, string> Headers);

public sealed record NetworkStorageShadowComparison(
    int? AuthoritativeStatusCode,
    int ShadowStatusCode,
    string? AuthoritativeErrorCode,
    string? ShadowErrorCode,
    IReadOnlyDictionary<string, string> AuthoritativeHeaders,
    IReadOnlyDictionary<string, string> ExpectedHeaders,
    bool StatusCodeMatches,
    bool ErrorCodeMatches,
    bool HeadersMatch,
    IReadOnlyDictionary<string, string?> HeaderDifferences,
    NetworkStorageShadowReplayDiagnosticsComparison? ReplayDiagnostics);

public sealed record NetworkStorageShadowReplayDiagnosticsComparison(
    string? AuthoritativeCompatibilityAlias,
    string ShadowCompatibilityAlias,
    bool CompatibilityAliasMatches,
    string? AuthoritativeClassification,
    string ShadowClassification,
    bool ClassificationMatches,
    string? AuthoritativeContentType,
    string? ShadowContentType,
    bool ContentTypeMatches,
    string? AuthoritativeBodyKind,
    string ShadowBodyKind,
    bool BodyKindMatches,
    string? AuthoritativeBodyShape,
    string? ShadowBodyShape,
    bool BodyShapeMatches,
    IReadOnlyList<string> AuthoritativeQueryKeys,
    IReadOnlyList<string> ShadowQueryKeys,
    bool QueryKeysMatch,
    IReadOnlyDictionary<string, bool> AuthoritativeAuthSignals,
    IReadOnlyDictionary<string, bool> ShadowAuthSignals,
    bool AuthSignalsMatch);

/// <summary>Resolved runtime mode and dispatch target for a shadow-classified request.</summary>
public sealed record NetworkStorageShadowDispatch(
    string RuntimeMode,
    string DispatchTarget,
    bool ExecuteShadowCandidate,
    bool ProductionWritesAllowed,
    string Reason);

/// <summary>Catalog classification of a shadow-classified request (family/plane/disposition + alias normalization).</summary>
public sealed record NetworkStorageShadowRouteCatalogInfo(
    bool Matched,
    string Method,
    string? Template,
    string? Family,
    string? Plane,
    string? Disposition,
    bool IsMirrored,
    string CanonicalPath,
    string Alias,
    bool IsCompatibilityAlias);

/// <summary>
/// Native-candidate execution status and bounded side-effect diagnostics. Until a route family's
/// candidate handler is implemented, <see cref="Executed"/> is false and the side-effect lists are
/// empty; the structure exists so candidate handlers can report read/intended-write paths and
/// confirm production writes were suppressed.
/// </summary>
public sealed record NetworkStorageShadowCandidateExecution(
    bool Executed,
    string Status,
    string? AuthDecision,
    IReadOnlyList<string> StoragePathsRead,
    IReadOnlyList<string> IntendedWritePaths,
    bool SideEffectsSuppressed,
    string? SuppressionReason);

public sealed record NetworkStorageShadowResponse(
    bool Handled,
    bool Mutating,
    bool WritesEnabled,
    string RouteFamily,
    string? ErrorCode,
    NetworkStorageShadowComparison? Comparison,
    IReadOnlyDictionary<string, object?> Diagnostics,
    NetworkStorageShadowDispatch Dispatch,
    NetworkStorageShadowRouteCatalogInfo RouteCatalog,
    NetworkStorageShadowCandidateExecution CandidateExecution);
