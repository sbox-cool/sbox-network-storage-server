using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Usage;

/// <summary>
/// Classification of a metered data-plane request. Mirrors the legacy Bun
/// tracker's <c>read | write | endpoint</c> types plus explicit query/auth
/// buckets for the .NET route surface.
/// </summary>
public enum UsageKind
{
    /// <summary>Record/data read (GET record, records list, load).</summary>
    Read,
    /// <summary>Record/data write or delete (POST/DELETE record, append, analytics event).</summary>
    Write,
    /// <summary>Custom endpoint execution (/v3/endpoints). Counts toward reads (GET) or writes (else) too, per legacy rule.</summary>
    EndpointCall,
    /// <summary>Query execution (/v3/queries). Counts toward reads.</summary>
    Query,
    /// <summary>Auth-session operation (/v3/sessions). Counts requests/bytes only.</summary>
    Auth,
}

public readonly record struct NetworkStorageUsageTrackerSnapshot(
    int PendingBucketCount,
    int ConsecutiveFlushFailures,
    long TotalTrackedRequests,
    long TotalTrackedBytesIn,
    long TotalTrackedBytesOut,
    DateTimeOffset? LastTrackedAt,
    DateTimeOffset? LastSuccessfulFlushAt,
    DateTimeOffset? LastFailedFlushAt,
    DateTimeOffset? NextFlushRetryAt);
/// <summary>
/// In-process buffered usage metering for the Network Storage data plane — the
/// .NET port of the decommissioned Bun <c>tools/sbox/usage-tracker.js</c>.
/// <see cref="Track"/> accumulates per-(project, month, day, endpoint) deltas in
/// memory; <see cref="FlushAsync(bool, CancellationToken)"/> drains them into the
/// ScyllaDB counter tables (<c>project_usage_monthly/daily/endpoints</c>).
///
/// <para>Failure semantics match legacy: a failed bucket flush re-merges the
/// deltas into the live buffer and applies exponential backoff (30 s base,
/// 5 min cap) before the next non-forced flush. A hard process crash loses at
/// most one flush interval of telemetry — identical to the Bun tracker.</para>
///
/// <para>Thread-safety: buckets are locked for the few field additions; a
/// bucket being drained is marked <c>Draining</c> under its lock so a racing
/// <see cref="Track"/> retries and lands in a fresh bucket — no deltas are
/// lost to the drain race.</para>

/// </summary>
public sealed class NetworkStorageUsageTracker(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<NetworkStorageUsageTracker> logger)
{
    internal const int FlushIntervalSeconds = 30;
    internal const int MaxBufferKeys = 50;
    private const long BackoffBaseMs = 30_000;
    private const long BackoffMaxMs = 5 * 60_000;

    private readonly ConcurrentDictionary<UsageKey, Bucket> _buffer = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private int _consecutiveFlushFailures;
    private long _nextFlushAtUnixMs;
    private long _totalTrackedRequests;
    private long _totalTrackedBytesIn;
    private long _totalTrackedBytesOut;
    private long _lastTrackedAtUnixMs;
    private long _lastSuccessfulFlushAtUnixMs;
    private long _lastFailedFlushAtUnixMs;


    /// <summary>Number of distinct pending buckets (diagnostics/tests).</summary>
    public int PendingBucketCount => _buffer.Count;
    public NetworkStorageUsageTrackerSnapshot GetSnapshot() => new(
        PendingBucketCount,
        Volatile.Read(ref _consecutiveFlushFailures),
        Interlocked.Read(ref _totalTrackedRequests),
        Interlocked.Read(ref _totalTrackedBytesIn),
        Interlocked.Read(ref _totalTrackedBytesOut),
        FromUnixMilliseconds(Interlocked.Read(ref _lastTrackedAtUnixMs)),
        FromUnixMilliseconds(Interlocked.Read(ref _lastSuccessfulFlushAtUnixMs)),
        FromUnixMilliseconds(Interlocked.Read(ref _lastFailedFlushAtUnixMs)),
        FromUnixMilliseconds(Interlocked.Read(ref _nextFlushAtUnixMs)));


    /// <summary>
    /// Record one data-plane request. Never throws; a metering failure must
    /// never affect the request being metered.
    /// </summary>
    public void Track(
        string projectId,
        UsageKind kind,
        string method,
        long bytesIn,
        long bytesOut,
        double durationMs,
        string? endpointSlug = null,
        bool isError = false,
        long storageDeltaBytes = 0)
    {
        if (string.IsNullOrEmpty(projectId)) return;
        try
        {
            var now = time.GetUtcNow();
            var key = new UsageKey(
                projectId,
                now.ToString("yyyy-MM"),
                now.ToString("yyyy-MM-dd"),
                endpointSlug ?? string.Empty);

            while (true)
            {
                var bucket = _buffer.GetOrAdd(key, static _ => new Bucket());
                lock (bucket)
                {
                    if (bucket.Draining) continue; // being flushed — retry into a fresh bucket

                    bucket.Requests++;
                    switch (kind)
                    {
                        case UsageKind.Read:
                            bucket.Reads++;
                            break;
                        case UsageKind.Write:
                            bucket.Writes++;
                            break;
                        case UsageKind.EndpointCall:
                            bucket.EndpointCalls++;
                            // Endpoints perform reads and often writes internally —
                            // count toward both based on HTTP method (legacy rule).
                            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) bucket.Reads++;
                            else bucket.Writes++;
                            break;
                        case UsageKind.Query:
                            bucket.Reads++;
                            break;
                        case UsageKind.Auth:
                            break;
                    }
                    bucket.BytesIn += Math.Max(0, bytesIn);
                    bucket.BytesOut += Math.Max(0, bytesOut);
                    if (isError) bucket.Errors++;
                    if (durationMs >= 0)
                    {
                        var roundedDurationMs = (long)Math.Round(durationMs);
                        bucket.DurationMsSum += roundedDurationMs;
                        bucket.DurationSamples++;
                        // One compute unit is one millisecond of observed request
                        // work, rounded up so sub-millisecond requests are not free.
                        bucket.ComputeUnits += Math.Max(1, (long)Math.Ceiling(durationMs));
                    }
                    bucket.StorageDeltaBytes += storageDeltaBytes;
                    break;
                }
            }
            Interlocked.Increment(ref _totalTrackedRequests);
            Interlocked.Add(ref _totalTrackedBytesIn, Math.Max(0, bytesIn));
            Interlocked.Add(ref _totalTrackedBytesOut, Math.Max(0, bytesOut));
            Interlocked.Exchange(ref _lastTrackedAtUnixMs, now.ToUnixTimeMilliseconds());


            // Auto-flush when the buffer grows large (legacy MAX_BUFFER_KEYS).
            if (_buffer.Count >= MaxBufferKeys)
            {
                _ = FlushAsync(force: false, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Usage tracking failed for project {ProjectId} — request unaffected", projectId);
        }
    }

    /// <summary>Flush respecting the failure backoff window.</summary>
    public Task FlushAsync(CancellationToken ct = default) => FlushAsync(force: false, ct);

    /// <summary>
    /// Drain all pending buckets into ScyllaDB. <paramref name="force"/> ignores
    /// the failure backoff (used on shutdown). Single-flight: a concurrent call
    /// returns immediately. Never throws.
    /// </summary>
    public async Task FlushAsync(bool force, CancellationToken ct)
    {
        try
        {
            if (!force && time.GetUtcNow().ToUnixTimeMilliseconds() < Interlocked.Read(ref _nextFlushAtUnixMs))
                return;
            if (!await _flushGate.WaitAsync(0, ct))
                return;

            try
            {
                // INetworkStorageStore is registered scoped; the tracker is a
                // process-wide singleton, so resolve the store in a scope per
                // flush pass (one scope for the whole drain, not per bucket).
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
                var anyFailure = false;
                var anySuccess = false;
                foreach (var key in _buffer.Keys)
                {
                    if (!_buffer.TryRemove(key, out var bucket)) continue;

                    UsageDelta delta;
                    lock (bucket)
                    {
                        bucket.Draining = true;
                        delta = bucket.ToDelta();
                    }
                    if (IsZero(delta)) continue;

                    try
                    {
                        await store.IncrementProjectUsageAsync(
                            key.ProjectId, key.Month, key.Day,
                            key.EndpointSlug.Length == 0 ? null : key.EndpointSlug,
                            delta, ct);
                        anySuccess = true;
                    }
                    catch (Exception ex)
                    {
                        anyFailure = true;
                        Requeue(key, delta);
                        logger.LogWarning(ex, "Usage flush failed for {ProjectId} {Month} — deltas re-queued", key.ProjectId, key.Month);
                    }
                }

                if (anyFailure)
                {
                    var failures = Interlocked.Increment(ref _consecutiveFlushFailures);
                    var backoffMs = Math.Min(BackoffBaseMs * (1L << Math.Min(failures - 1, 4)), BackoffMaxMs);
                    Interlocked.Exchange(ref _nextFlushAtUnixMs, time.GetUtcNow().ToUnixTimeMilliseconds() + backoffMs);
                    Interlocked.Exchange(ref _lastFailedFlushAtUnixMs, time.GetUtcNow().ToUnixTimeMilliseconds());
                }
                else
                {
                    Interlocked.Exchange(ref _consecutiveFlushFailures, 0);
                    Interlocked.Exchange(ref _nextFlushAtUnixMs, 0);
                }
                if (anySuccess)
                    Interlocked.Exchange(ref _lastSuccessfulFlushAtUnixMs, time.GetUtcNow().ToUnixTimeMilliseconds());
            }
            finally
            {
                _flushGate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Usage flush aborted");
        }
    }

    /// <summary>Re-merge a failed bucket's deltas into the live buffer.</summary>
    private void Requeue(UsageKey key, UsageDelta delta)
    {
        while (true)
        {
            var bucket = _buffer.GetOrAdd(key, static _ => new Bucket());
            lock (bucket)
            {
                if (bucket.Draining) continue;
                bucket.Requests += delta.Requests;
                bucket.Reads += delta.Reads;
                bucket.Writes += delta.Writes;
                bucket.EndpointCalls += delta.EndpointCalls;
                bucket.BytesIn += delta.BytesIn;
                bucket.BytesOut += delta.BytesOut;
                bucket.Errors += delta.Errors;
                bucket.DurationMsSum += delta.DurationMsSum;
                bucket.DurationSamples += delta.DurationSamples;
                bucket.ComputeUnits += delta.ComputeUnits;
                bucket.StorageDeltaBytes += delta.StorageDeltaBytes;
                return;
            }
        }
    }
    private static DateTimeOffset? FromUnixMilliseconds(long value)
        => value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;


    private static bool IsZero(in UsageDelta d)
        => d.Requests == 0 && d.Reads == 0 && d.Writes == 0 && d.EndpointCalls == 0
           && d.BytesIn == 0 && d.BytesOut == 0 && d.Errors == 0
           && d.DurationMsSum == 0 && d.DurationSamples == 0 && d.ComputeUnits == 0
           && d.StorageDeltaBytes == 0;

    private readonly record struct UsageKey(string ProjectId, string Month, string Day, string EndpointSlug);

    private sealed class Bucket
    {
        public bool Draining;
        public long Requests, Reads, Writes, EndpointCalls, BytesIn, BytesOut, Errors;
        public long DurationMsSum, DurationSamples, ComputeUnits, StorageDeltaBytes;

        public UsageDelta ToDelta() => new(
            Requests, Reads, Writes, EndpointCalls, BytesIn, BytesOut, Errors,
            DurationMsSum, DurationSamples, StorageDeltaBytes, ComputeUnits);
    }
}
