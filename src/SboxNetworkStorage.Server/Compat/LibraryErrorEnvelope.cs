using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Endpoints;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// The error envelope Network Storage library callers receive on endpoint routes (docs/compatibility.md).
/// s&amp;box's Http API does not expose the body of a non-2xx response, so a library caller sees only the status
/// code and loses the error code, message and request id. Endpoint routes therefore answer library callers with
/// HTTP 200 and the real status in the body, as the reference backend's epError does. Other HTTP clients keep
/// their real statuses. Success responses are never wrapped.
/// </summary>
public static class LibraryErrorEnvelope
{
    /// <summary>Items key set when the body of an endpoint request carries a <c>security</c> object (old revisions).</summary>
    private const string BodyCallerKey = "sboxcool.library_caller_body";

    /// <summary>Items key holding the real status of an envelope error (the wire status is 200).</summary>
    private const string ReportedStatusKey = "sboxcool.library_error_status";

    /// <summary>Route metadata that marks an endpoint route whose errors follow the library envelope.</summary>
    public sealed class EndpointRouteMarker
    {
        public static readonly EndpointRouteMarker Instance = new();
        private EndpointRouteMarker() { }
    }

    /// <summary>Marks an endpoint route. Applied to every execution and slug-read route.</summary>
    public static TBuilder AsLibraryEnvelopeRoute<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(EndpointRouteMarker.Instance);
        return builder;
    }

    /// <summary>True when the matched route is an endpoint route.</summary>
    public static bool IsEndpointRoute(HttpContext context)
        => context.GetEndpoint()?.Metadata.GetMetadata<EndpointRouteMarker>() is not null;

    /// <summary>
    /// True when the request identifies a Network Storage library build: a non-empty <c>x-ns-library-version</c>
    /// (any value), a recognised <c>x-ns-client-type</c> (game, editor or dedicated), or a body <c>security</c>
    /// object (see <see cref="MarkBodyCaller"/>). Every library build published so far sends
    /// <c>x-ns-client-type</c>, so header-only GET calls are identified as well.
    /// </summary>
    public static bool IsLibraryCaller(HttpContext context)
    {
        if (context.Items.ContainsKey(BodyCallerKey)) return true;
        if (ClientIdentityContext.Get(context).ClientType is not null) return true;
        return context.Request.Headers.TryGetValue(ClientHeaders.LibraryVersion, out var values)
            && values.Any(value => !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>Records that the request body is a library call (old revisions post a <c>security</c> object).</summary>
    public static void MarkBodyCaller(HttpContext context) => context.Items[BodyCallerKey] = true;

    /// <summary>True when an error on this request must use the library envelope.</summary>
    public static bool UsesEnvelope(HttpContext context) => IsEndpointRoute(context) && IsLibraryCaller(context);

    /// <summary>
    /// The status an envelope error reports: the real status, not the 200 on the wire. Otherwise the response
    /// status. Used by usage statistics so a library error still counts as the error it is.
    /// </summary>
    public static int ReportedStatus(HttpContext context)
        => context.Items[ReportedStatusKey] is int status ? status : context.Response.StatusCode;

    /// <summary>
    /// Builds the envelope body: <c>ok</c>, <c>status</c> (the real status), <c>error</c> (<c>code</c>,
    /// <c>message</c>, <c>requestId</c>, plus <paramref name="errorFields"/>), top-level <c>message</c> and
    /// <c>_requestId</c>. A non-null <paramref name="security"/> (a gate denial) is added to <c>error</c> and the
    /// body, and the body is marked <c>retrySafe</c>.
    /// </summary>
    public static Dictionary<string, object?> Build(HttpContext context, int status, string code, string message,
        IReadOnlyDictionary<string, object?>? errorFields = null, object? security = null)
    {
        var requestId = context.TraceIdentifier;
        var error = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["message"] = message,
            ["requestId"] = requestId,
        };
        if (errorFields is not null)
        {
            foreach (var (key, value) in errorFields)
                error[key] = value;
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ok"] = false,
            ["status"] = status,
            ["error"] = error,
            ["message"] = message,
            ["_requestId"] = requestId,
        };
        if (security is not null)
        {
            error["security"] = security;
            body["security"] = security;
            body["retrySafe"] = true;
        }

        return body;
    }

    /// <summary>Writes a library-envelope error: HTTP 200 with the real status in the body.</summary>
    public static Task WriteAsync(HttpContext context, int status, string code, string message,
        object? security = null)
        => WriteBodyAsync(context, status, Build(context, status, code, message, security: security));

    /// <summary>
    /// Writes the envelope for a native handler result whose body is the <c>{error:{code,message}}</c> shape.
    /// </summary>
    public static Task WriteNativeAsync(HttpContext context, int status, object? body)
    {
        var root = body is null ? default : JsonSerializer.SerializeToElement(body);
        var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var element) ? element : default;
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
        return WriteAsync(context, status, code ?? "REQUEST_FAILED", message ?? "The request failed.");
    }

    /// <summary>
    /// Writes an already-built library body with HTTP 200, <c>X-Request-Id</c>, and <c>_revisionStatus</c> when the
    /// request resolved a revision status. <paramref name="status"/> is the real status: it is kept for usage
    /// statistics and is not sent on the wire. Used by callers that merge their own body (the executor path).
    /// </summary>
    public static Task WriteBodyAsync(HttpContext context, int status, object body)
    {
        context.Items[ReportedStatusKey] = status;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
        object payload = body;
        // A v2 body is already sealed, and ResponseBodyFor put _revisionStatus inside it. Adding a field here would
        // change the sealed outer body, so inject only for unsealed bodies.
        if (context.Items[EncryptedRequestV2.ItemKey] is not EncryptedRequestV2.ResponseKey
            && context.Items["RevisionStatus"] is Dictionary<string, object?> revisionStatus)
            payload = RevisionStatusInjector.WithStatus(payload, revisionStatus);
        return context.Response.WriteAsJsonAsync(payload, context.RequestAborted);
    }
}
