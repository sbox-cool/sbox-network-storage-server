namespace SboxNetworkStorage.Server.Middleware;

public static class CorrelationContext
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "sboxcool.correlation_id";

    public static string Get(HttpContext context)
        => context.Items.TryGetValue(ItemKey, out var value) ? Convert.ToString(value) ?? string.Empty : string.Empty;
}

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Items[CorrelationContext.ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationContext.HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        await next(context);
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        var existing = context.Request.Headers[CorrelationContext.HeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(existing) && existing.Length <= 128) return existing;
        return $"req_{Guid.NewGuid():N}";
    }
}
