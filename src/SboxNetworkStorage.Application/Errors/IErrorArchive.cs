using SboxNetworkStorage.Contracts.Errors;

namespace SboxNetworkStorage.Application.Errors;

public interface IErrorArchive
{
    Task<CapturedErrorDto> CaptureAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent <em>unresolved</em> captured errors, newest first.
    /// Errors marked resolved via <see cref="MarkResolvedAsync"/> are excluded so
    /// the admin dashboard surfaces only open issues instead of an ever-growing
    /// list of already-fixed exceptions.
    /// </summary>
    Task<IReadOnlyList<CapturedErrorDto>> ListRecentAsync(int limit, CancellationToken cancellationToken);
    Task<InternalErrorArchiveResult> ListAsync(InternalErrorArchiveQuery query, CancellationToken cancellationToken);


    /// <summary>Returns a single captured error by id regardless of resolution state.</summary>
    Task<CapturedErrorDto?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the error as resolved (records who and an optional note). Returns
    /// <see langword="true"/> when an unresolved error with that id was updated,
    /// <see langword="false"/> when no matching unresolved error exists.
    /// </summary>
    Task<bool> MarkResolvedAsync(string id, long resolvedByUserId, string? note, CancellationToken cancellationToken);

    /// <summary>
    /// Records the outcome of attempting to deliver this captured error to the
    /// external alert channel (Discord) so <c>/admin/errors</c> truthfully shows
    /// whether each error reached the channel. <paramref name="delivered"/> sets
    /// <c>discord_sent</c>; <paramref name="statusCode"/> records the HTTP status
    /// returned by the webhook (<c>0</c> when the request threw before any
    /// response). Returns <see langword="true"/> when a matching row was updated.
    /// </summary>
    Task<bool> MarkAlertDeliveredAsync(string id, bool delivered, int statusCode, CancellationToken cancellationToken);
}

/// <summary>
/// Primary, cross-instance error archive (PostgreSQL). <see cref="IsConfigured"/>
/// reports whether a backing database is available so callers can decide whether
/// to attempt the primary store before falling back to a local archive.
/// </summary>
public interface IErrorArchiveBackend : IErrorArchive
{
    bool IsConfigured { get; }
}

/// <summary>
/// Local, per-instance fallback archive (NDJSON file) used when the primary
/// archive is unavailable. Always writable, so an unexpected error is never lost
/// on the node that captured it even when the database is the failing dependency.
/// </summary>
public interface IFallbackErrorArchive : IErrorArchive
{
}

public sealed class InMemoryErrorArchive : IErrorArchive
{
    private readonly List<CapturedErrorDto> errors = [];
    private readonly Lock gate = new();

    public Task<CapturedErrorDto> CaptureAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            errors.Insert(0, capturedError);
            if (errors.Count > 500) errors.RemoveRange(500, errors.Count - 500);
        }

        return Task.FromResult(capturedError);
    }

    public Task<IReadOnlyList<CapturedErrorDto>> ListRecentAsync(int limit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<CapturedErrorDto>>(
                errors.Where(error => error.ResolvedAt is null)
                    .Take(Math.Clamp(limit, 1, 100))
                    .Select(CapturedErrorArchiveSupport.ForList)
                    .ToArray());
        }
    }


    public Task<InternalErrorArchiveResult> ListAsync(InternalErrorArchiveQuery query, CancellationToken cancellationToken)
    {
        var normalized = CapturedErrorArchiveSupport.NormalizeQuery(query);
        var now = DateTimeOffset.UtcNow;
        CapturedErrorDto[] snapshot;
        lock (gate)
        {
            snapshot = errors
                .Where(error => CapturedErrorArchiveSupport.Matches(error, normalized, now))
                .OrderByDescending(error => error.Timestamp)
                .ToArray();
        }

        var page = snapshot.Skip(normalized.Offset).Take(normalized.Limit + 1).ToArray();
        return Task.FromResult(new InternalErrorArchiveResult(
            page.Take(normalized.Limit).Select(CapturedErrorArchiveSupport.ForList).ToArray(),
            page.Length > normalized.Limit,
            "memory"));
    }

    public Task<CapturedErrorDto?> GetAsync(string id, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(errors.FirstOrDefault(error => string.Equals(error.Id, id, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<bool> MarkResolvedAsync(string id, long resolvedByUserId, string? note, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var index = errors.FindIndex(error =>
                string.Equals(error.Id, id, StringComparison.OrdinalIgnoreCase) && error.ResolvedAt is null);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            errors[index] = errors[index] with { ResolvedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkAlertDeliveredAsync(string id, bool delivered, int statusCode, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var index = errors.FindIndex(error => string.Equals(error.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            errors[index] = errors[index] with { DiscordSent = delivered, DiscordStatus = statusCode };
            return Task.FromResult(true);
        }
    }
}
