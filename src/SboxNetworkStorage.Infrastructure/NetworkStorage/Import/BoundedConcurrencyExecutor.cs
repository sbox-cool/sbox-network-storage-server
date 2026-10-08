using System.Runtime.ExceptionServices;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Import;

/// <summary>
/// Runs an async action over many items with a bounded number in flight at once,
/// turning O(n) serial round-trips into O(n / concurrency). Fail-fast: on the
/// first action failure it stops dispatching new work and, after the in-flight
/// actions drain, rethrows that original exception (preserving its stack) rather
/// than a cancellation that masks it.
/// </summary>
internal static class BoundedConcurrencyExecutor
{
    public static async Task RunAsync<T>(
        IReadOnlyList<T> items,
        int maxInFlight,
        Func<T, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        if (items is null || items.Count == 0) return;
        var limit = Math.Clamp(maxInFlight, 1, 1024);

        using var gate = new SemaphoreSlim(limit, limit);
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inFlight = new List<Task>(items.Count);

        ExceptionDispatchInfo? firstError = null;
        var errorLock = new object();

        async Task RunOneAsync(T item)
        {
            try
            {
                await action(item, failure.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (errorLock)
                {
                    // The action that originally failed captures itself first
                    // (it cancels only afterwards), so cancellation-induced
                    // exceptions on sibling actions never displace the real cause.
                    firstError ??= ExceptionDispatchInfo.Capture(ex);
                }
                failure.Cancel();
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            foreach (var item in items)
            {
                await gate.WaitAsync(failure.Token).ConfigureAwait(false);
                inFlight.Add(RunOneAsync(item));
            }
        }
        catch (OperationCanceledException)
        {
            // A worker faulted (or the caller cancelled) and tripped the gate's
            // token; stop dispatching and drain what is already running.
        }

        await Task.WhenAll(inFlight).ConfigureAwait(false);
        firstError?.Throw();
    }
}
