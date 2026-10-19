namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Marks an <see cref="EndpointScriptHost"/> method as part of the script API. <paramref name="name"/> is the dotted
/// path scripts call (for example <c>records.get</c> is <c>ns.records.get</c>). The API reference
/// (<c>wwwroot/js/endpoint-api/ns.d.ts</c>) must document exactly this set; a test keeps them in step.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ScriptApiAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>
    /// True for members that only the script prelude calls (for example <c>workflows.catalog</c>). They are not part
    /// of the documented script API, so the typings and the drift test skip them.
    /// </summary>
    public bool Internal { get; set; }
}

/// <summary>
/// Thrown by host bindings for a failure the script must not be able to catch or rewrite: the endpoint answers
/// with this status, code and message, whatever the script does afterwards.
/// </summary>
public sealed class EndpointScriptFailureException(int status, string code, string message, string? ownerDetail = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? OwnerDetail { get; } = ownerDetail;
}
