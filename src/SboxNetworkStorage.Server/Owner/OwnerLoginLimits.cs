using System.Threading.RateLimiting;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// The <c>owner-login</c> rate-limit policy: a per-client window chained to limits shared by every client, so guesses
/// spread over many addresses are capped too and at most a few password hashes (about 210k PBKDF2 rounds each) run at once.
/// </summary>
public sealed class OwnerLoginLimits : IDisposable
{
    public const string Policy = "owner-login";
    public const int PerClientPerMinute = 10;
    public const int AllClientsPerMinute = 60;
    public const int ConcurrentRequests = 2;
    public const int QueuedRequests = 8;

    private readonly FixedWindowRateLimiter allClients = new(new FixedWindowRateLimiterOptions
    { PermitLimit = AllClientsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });

    private readonly ConcurrencyLimiter concurrent = new(new ConcurrencyLimiterOptions
    { PermitLimit = ConcurrentRequests, QueueLimit = QueuedRequests, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });

    public RateLimitPartition<string> Partition(HttpContext context) => RateLimitPartition.Get(ClientAddress.Resolve(context), _ =>
    {
        var perClient = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        { PermitLimit = PerClientPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
        // Order matters: a failed link releases the leases before it, but a used window permit is never returned.
        // Waiting for a free slot first and checking the client's own window before the shared one keeps one noisy
        // address from spending the shared allowance.
        return new ClientLimiter(perClient, RateLimiter.CreateChained(concurrent, perClient, allClients));
    });

    public void Dispose()
    {
        allClients.Dispose();
        concurrent.Dispose();
    }

    /// <summary>
    /// Acquires through the whole chain but reports the per-client idle time: the rate limiter middleware drops idle
    /// partitions, and the shared limiters must not make a client's partially used window look idle.
    /// </summary>
    private sealed class ClientLimiter(RateLimiter perClient, RateLimiter chain) : RateLimiter
    {
        public override TimeSpan? IdleDuration => perClient.IdleDuration;

        public override RateLimiterStatistics? GetStatistics() => perClient.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => chain.AttemptAcquire(permitCount);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
            => chain.AcquireAsync(permitCount, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                chain.Dispose();
                perClient.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
