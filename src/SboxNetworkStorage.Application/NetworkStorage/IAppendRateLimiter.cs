using System.Threading;
using System.Threading.Tasks;

namespace SboxNetworkStorage.Application.NetworkStorage;

public sealed record AppendRateLimitResult(bool Allowed, string? Code = null, string? Message = null)
{
    public static AppendRateLimitResult AllowedResult { get; } = new(true);
}

/// <summary>
/// Per-process daily rate limiter for Network Storage global-collection appends.
/// Mirrors the legacy server <c>tools/sbox/rate-limit.js</c> semantics: two modes,
/// <c>"player"</c> (per writer per collection per day) and <c>"collection"</c>
/// (total collection writes per day), with a configurable maximum per day and
/// counters that reset at midnight UTC.
/// </summary>
public interface IAppendRateLimiter
{
    Task<AppendRateLimitResult> CheckAsync(
        string projectId,
        string collectionId,
        string mode,
        int savesPerDay,
        string writerId,
        CancellationToken cancellationToken);
}
