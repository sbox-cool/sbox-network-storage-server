namespace SboxNetworkStorage.EndpointSpike.Compiler;

/// <summary>Deterministic management diagnostic. Line/column are 1-based.</summary>
public sealed record NsDiagnostic(string Code, string Message, int Line, int Column);

/// <summary>Internal fail-fast carrier; the first diagnostic aborts compilation so output is deterministic.</summary>
internal sealed class NsCompileException : Exception
{
    public NsCompileException(NsDiagnostic diagnostic) : base(diagnostic.Message)
    {
        Diagnostic = diagnostic;
    }

    public NsDiagnostic Diagnostic { get; }
}

public sealed record CompileOutcome(NsDiagnostic? Error, CompiledEndpoint? Endpoint)
{
    public bool Ok => Error is null && Endpoint is not null;

    public static CompileOutcome Fail(NsDiagnostic diagnostic) => new(diagnostic, null);

    public static CompileOutcome Success(CompiledEndpoint endpoint) => new(null, endpoint);
}
