using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Keyring file reload when the keys file goes missing: the last good snapshot keeps serving, one warning is logged
/// per missing transition, and a store with no snapshot fails closed. Time is injected, so no test waits for the
/// five-second check interval.
/// </summary>
public sealed class RequestEncryptionKeyStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sbox-ns-keystore-" + Guid.NewGuid().ToString("N"));
    private readonly string _keysPath;
    private readonly WarningCountingLogger _logger = new();
    private long _ticks;

    public RequestEncryptionKeyStoreTests()
    {
        Directory.CreateDirectory(_folder);
        _keysPath = Path.Combine(_folder, "request_encryption_keys.json");
        RequestEncryptionKeyring.EnsureAndLoad(_keysPath, Now);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private RequestEncryptionKeyStore NewStore() => new(_keysPath, _logger, CheckInterval, () => _ticks);

    /// <summary>Moves the injected clock past the check interval so the next <c>Current()</c> re-checks the file.</summary>
    private void PastCheckInterval() => _ticks += Stopwatch.Frequency * 6;

    [Fact]
    public void Missing_file_keeps_the_last_good_snapshot()
    {
        var store = NewStore();
        var loaded = store.Current();

        File.Delete(_keysPath);
        PastCheckInterval();

        Assert.Same(loaded, store.Current());
        Assert.Equal(1, _logger.Warnings);
    }

    [Fact]
    public void Missing_file_warns_once_per_transition_not_per_check()
    {
        var store = NewStore();
        store.Current();
        var backup = File.ReadAllBytes(_keysPath);

        File.Delete(_keysPath);
        for (var check = 0; check < 3; check++)
        {
            PastCheckInterval();
            store.Current();
        }

        Assert.Equal(1, _logger.Warnings);

        // Restored, then removed again: a new transition, so one more warning.
        File.WriteAllBytes(_keysPath, backup);
        PastCheckInterval();
        store.Current();

        File.Delete(_keysPath);
        PastCheckInterval();
        store.Current();

        Assert.Equal(2, _logger.Warnings);
    }

    [Fact]
    public void Missing_file_with_no_snapshot_fails_closed()
    {
        File.Delete(_keysPath);
        var store = NewStore();

        Assert.ThrowsAny<IOException>(() => store.Current());
        Assert.Equal(0, _logger.Warnings);
    }

    private sealed class WarningCountingLogger : ILogger<RequestEncryptionKeyStore>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }
}
