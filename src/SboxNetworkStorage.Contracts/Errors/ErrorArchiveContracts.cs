namespace SboxNetworkStorage.Contracts.Errors;

public sealed record CapturedErrorDto(
    string Id,
    DateTimeOffset Timestamp,
    string Source,
    string Method,
    string Path,
    int StatusCode,
    string Classification,
    string CorrelationId,
    string Message,
    string? StackTrace,
    DateTimeOffset? ResolvedAt = null,
    string? Fingerprint = null,
    string? ProjectId = null,
    long? UserId = null,
    string? SteamId = null,
    string? StorageBackend = null,
    bool? DiscordSent = null,
    int? DiscordStatus = null,
    IReadOnlyList<string>? Tags = null,
    string? PayloadJson = null);

public sealed record InternalErrorArchiveQuery(
    string Query = "",
    string Source = "",
    string ProjectId = "",
    string Range = "all",
    int Limit = 50,
    int Offset = 0);

public sealed record InternalErrorArchiveResult(
    IReadOnlyList<CapturedErrorDto> Errors,
    bool HasMore,
    string Backend,
    string? BackendError = null);
