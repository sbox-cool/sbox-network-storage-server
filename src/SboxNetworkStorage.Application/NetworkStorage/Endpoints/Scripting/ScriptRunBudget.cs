using Jint;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// One budget for a whole scripted request: every engine entry (module evaluation, handler call, serialisation) and
/// every re-run round draws from it. Jint resets its own <c>TimeoutInterval</c> and <c>MaxStatements</c> at each
/// top-level entry, so those cannot express a per-request limit; this class holds the shared state instead.
/// </summary>
/// <remarks>
/// Covers: wall-clock time, statements executed across all engines (<see cref="EndpointScriptLimits.MaxStatements"/>),
/// and workflow depth and steps (see <see cref="Workflows"/>).
/// The clock starts at <see cref="Start"/>, which the runtime calls once engine setup is done and user code is about
/// to run. Engine construction (the hardening script and the prelude) is not counted against the time budget, so a slow
/// host does not turn a valid script into a timeout. Store reads between re-run rounds are counted.
/// Memory is not shared: each round's engine gets its own <see cref="EndpointScriptLimits.MemoryBytes"/> limit, and
/// rounds run one at a time, so the peak is one engine's limit.
/// </remarks>
public sealed class ScriptRunBudget : IDisposable
{
    private readonly CancellationToken _client;
    private readonly CancellationTokenSource _deadline;
    private readonly int _maxStatements;
    private int _statements;
    private bool _started;

    public ScriptRunBudget(EndpointScriptLimits limits, CancellationToken clientToken)
    {
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _client = clientToken;
        _maxStatements = limits.MaxStatements;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(clientToken);
        Workflows = new ScriptWorkflowBudget(limits.Timeout);
    }

    /// <summary>The limits every engine of this request is configured with.</summary>
    public EndpointScriptLimits Limits { get; }

    /// <summary>Cancelled when the client disconnects, or when the time budget elapses after <see cref="Start"/>.</summary>
    public CancellationToken Token => _deadline.Token;

    /// <summary>True only when the client itself cancelled; a deadline expiry leaves this false.</summary>
    public bool ClientCancelled => _client.IsCancellationRequested;

    /// <summary>Workflow depth, steps and elapsed time for script-initiated workflow calls, shared by every round.</summary>
    public ScriptWorkflowBudget Workflows { get; }

    /// <summary>
    /// Arms the time budget. Idempotent: later rounds of the same request keep the first start. Call it after engine
    /// setup and before user code runs.
    /// </summary>
    internal void Start()
    {
        if (_started)
            return;
        _started = true;
        _deadline.CancelAfter(Limits.Timeout);
        Workflows.Start();
    }

    /// <summary>A constraint for one engine. Every engine of the request must use one from this budget.</summary>
    internal Constraint CreateStatementConstraint() => new StatementConstraint(this);

    private void ChargeStatement()
    {
        if (++_statements > _maxStatements)
            throw new ScriptLimitExceededException($"Statement limit exceeded: more than {_maxStatements} statements in one request.");
    }

    public void Dispose() => _deadline.Dispose();

    /// <summary>
    /// Counts statements exactly (not amortised). <see cref="Reset"/> is a no-op: Jint calls it at every entry, and the
    /// count must carry across entries and rounds.
    /// </summary>
    private sealed class StatementConstraint(ScriptRunBudget budget) : Constraint
    {
        public override void Check() => budget.ChargeStatement();

        public override void Reset()
        {
        }
    }
}
