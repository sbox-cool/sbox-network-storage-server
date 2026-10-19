namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Bounds how many scripted requests run at once, server-wide and per project. Entry never queues: a full gate returns
/// null at once, and the caller answers 503 <c>SCRIPT_BUSY</c>. Register one instance as a singleton; a per-request
/// instance would limit nothing.
/// </summary>
public sealed class ScriptConcurrencyGate
{
    /// <summary>Concurrent scripted requests allowed for one project.</summary>
    public const int DefaultPerProjectLimit = 2;

    private readonly SemaphoreSlim _global;
    private readonly int _perProjectLimit;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);

    /// <summary>Server-wide limit of one slot per processor, with <see cref="DefaultPerProjectLimit"/> per project.</summary>
    public ScriptConcurrencyGate()
        : this(Environment.ProcessorCount, DefaultPerProjectLimit)
    {
    }

    public ScriptConcurrencyGate(int globalLimit, int perProjectLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(globalLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perProjectLimit, 1);
        _global = new SemaphoreSlim(globalLimit, globalLimit);
        _perProjectLimit = perProjectLimit;
    }

    /// <summary>Takes a slot for <paramref name="projectId"/>, or returns null when the server or the project is full.</summary>
    public ScriptSlot? TryEnter(string projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        if (!_global.Wait(0))
            return null;

        lock (_sync)
        {
            _active.TryGetValue(projectId, out var count);
            if (count >= _perProjectLimit)
            {
                _global.Release();
                return null;
            }
            _active[projectId] = count + 1;
        }
        return new ScriptSlot(this, projectId);
    }

    private void Release(string projectId)
    {
        lock (_sync)
        {
            if (_active[projectId] <= 1)
                _active.Remove(projectId);
            else
                _active[projectId]--;
        }
        _global.Release();
    }

    /// <summary>A held slot. Disposing it releases the slot; a second Dispose does nothing.</summary>
    public sealed class ScriptSlot : IDisposable
    {
        private ScriptConcurrencyGate? _gate;
        private readonly string _projectId;

        internal ScriptSlot(ScriptConcurrencyGate gate, string projectId)
        {
            _gate = gate;
            _projectId = projectId;
        }

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release(_projectId);
        }
    }
}
