using System.Diagnostics;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Bounds nesting depth, step count and elapsed time for the workflows that a scripted request runs through
/// <c>ns.workflows.run</c>. <see cref="ScriptRunBudget"/> creates one per request and shares it across every round, so reruns count toward the same bounds.
/// Exceeding a bound throws <see cref="ScriptLimitExceededException"/>, which the host maps to <c>SCRIPT_LIMIT_EXCEEDED</c>; scripts cannot catch it.
/// </summary>
public sealed class ScriptWorkflowBudget
{
    /// <summary>Mirrors the YAML <c>MaxWorkflowDepth</c> so script-initiated nesting is bounded the same way.</summary>
    public const int DefaultMaxDepth = 5;

    /// <summary>Workflow steps per round across all nested workflows.</summary>
    public const int DefaultMaxSteps = 10_000;

    private readonly int _maxDepth;
    private readonly int _maxSteps;
    private readonly TimeSpan _timeout;
    private readonly Stopwatch _clock = new();
    private bool _started;
    private int _depth;
    private int _steps;

    public ScriptWorkflowBudget(TimeSpan timeout, int maxDepth = DefaultMaxDepth, int maxSteps = DefaultMaxSteps)
    {
        _timeout = timeout;
        _maxDepth = maxDepth;
        _maxSteps = maxSteps;
    }

    /// <summary>Starts the elapsed-time bound. Idempotent. Until this is called the time bound does not apply.</summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _clock.Start();
    }

    /// <summary>Stops the elapsed-time bound while the host waits on outbound HTTP. Idempotent.</summary>
    public void Pause() => _clock.Stop();

    /// <summary>Restarts the elapsed-time bound after <see cref="Pause"/>. Does nothing before <see cref="Start"/>.</summary>
    public void Resume()
    {
        if (_started)
            _clock.Start();
    }

    /// <summary>Called when a workflow starts. Throws when the nesting or time bound is exceeded.</summary>
    public void EnterWorkflow()
    {
        CheckElapsed();
        if (_depth >= _maxDepth)
            throw new ScriptLimitExceededException($"Workflow nesting depth exceeded (max {_maxDepth}).");
        _depth++;
    }

    /// <summary>Called when a workflow finishes, including on failure.</summary>
    public void ExitWorkflow() => _depth--;

    /// <summary>Called once per workflow step. Throws when the step or time bound is exceeded.</summary>
    public void ChargeStep()
    {
        CheckElapsed();
        if (++_steps > _maxSteps)
            throw new ScriptLimitExceededException($"Workflow step budget exceeded (max {_maxSteps} steps per invocation).");
    }

    private void CheckElapsed()
    {
        if (_started && _clock.Elapsed > _timeout)
            throw new ScriptLimitExceededException($"Workflow time budget exceeded ({_timeout.TotalSeconds:0.###} s).");
    }
}

/// <summary>Raised by a script host budget or limit check. Mapped to <c>SCRIPT_LIMIT_EXCEEDED</c>.</summary>
public sealed class ScriptLimitExceededException(string message) : Exception(message);
