using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Middleware;

namespace SboxNetworkStorage.Server.Tests;

public sealed class ExceptionHandlingMiddlewareOutageTests
{
    [Fact]
    public async Task PostgresAdminShutdown_MapsTo503_AndStaysVisible()
    {
        // Reproduces the reported production fault: PostgreSQL 57P01 on a /v3 gateway
        // request must surface as a retryable 503 (not a generic 500) while still
        // being archived and alerted so operators can find the stack.
        var archive = new RecordingArchive();
        var alert = new RecordingAlertSink();
        var fault = new PostgresException(
            "terminating connection due to administrator command", "FATAL", "FATAL", "57P01");
        var middleware = BuildMiddleware(_ => throw fault, archive, alert);
        var context = BuildContext("/v3/endpoints/abc/get-join");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("30", context.Response.Headers["Retry-After"].ToString());
        Assert.NotNull(archive.Captured);
        Assert.Equal(503, archive.Captured!.StatusCode);
    }

    [Fact]
    public async Task LogicError_StaysAs500()
    {
        // A unique-constraint violation is a real bug, not an outage: it must remain
        // a 500 so it is not silently downgraded to a "try again" 503.
        var archive = new RecordingArchive();
        var alert = new RecordingAlertSink();
        var fault = new PostgresException("duplicate key value", "ERROR", "ERROR", "23505");
        var middleware = BuildMiddleware(_ => throw fault, archive, alert);
        var context = BuildContext("/v3/endpoints/abc/get-join");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
    }

    private static ExceptionHandlingMiddleware BuildMiddleware(
        RequestDelegate next, IErrorArchive archive, IExceptionAlertSink alert)
        => new(next, archive, NullLogger<ExceptionHandlingMiddleware>.Instance);

    private static DefaultHttpContext BuildContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.RequestAborted = CancellationToken.None;
        context.Items[CorrelationContext.ItemKey] = "req_test_correlation";
        return context;
    }

    private sealed class RecordingArchive : IErrorArchive
    {
        public CapturedErrorDto? Captured { get; private set; }

        public Task<CapturedErrorDto> CaptureAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
        {
            Captured = capturedError;
            return Task.FromResult(capturedError);
        }

        public Task<IReadOnlyList<CapturedErrorDto>> ListRecentAsync(int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CapturedErrorDto>>([]);

        public Task<InternalErrorArchiveResult> ListAsync(InternalErrorArchiveQuery query, CancellationToken cancellationToken)
            => Task.FromResult(new InternalErrorArchiveResult([], false, "memory"));

        public Task<CapturedErrorDto?> GetAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult<CapturedErrorDto?>(null);

        public Task<bool> MarkResolvedAsync(string id, long resolvedByUserId, string? note, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> MarkAlertDeliveredAsync(string id, bool delivered, int statusCode, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class RecordingAlertSink : IExceptionAlertSink
    {
        public bool WasCalled { get; private set; }

        public Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }
    }
}
