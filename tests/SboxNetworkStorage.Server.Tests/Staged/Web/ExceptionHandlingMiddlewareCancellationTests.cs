using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Middleware;

namespace SboxNetworkStorage.Server.Tests;

public sealed class ExceptionHandlingMiddlewareCancellationTests
{
    [Fact]
    public async Task ReportsError_OnUncancellableToken_NotTheRequestToken()
    {
        var archive = new TokenRecordingArchive();
        var alert = new TokenRecordingAlertSink();
        var middleware = BuildMiddleware(archive, alert);
        var context = BuildContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.True(archive.WasCalled);
        Assert.False(archive.Token.CanBeCanceled);
    }

    [Fact]
    public async Task StillArchivesAndAlerts_WhenRequestAlreadyAborted()
    {
        // Reproduces the production failure: a slow request the client/proxy gave up
        // on. The archive throws when handed a cancelled token (as the PostgreSQL
        // command would), so reporting can only succeed on a detached token, and the
        // middleware must not rethrow when it can no longer write the page.
        var archive = new TokenRecordingArchive { ThrowIfCancelled = true };
        var alert = new TokenRecordingAlertSink();
        var middleware = BuildMiddleware(archive, alert);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var context = BuildContext();
        context.RequestAborted = cts.Token;

        await middleware.InvokeAsync(context);

        Assert.True(archive.WasCalled);
    }

    private static ExceptionHandlingMiddleware BuildMiddleware(IErrorArchive archive, IExceptionAlertSink alert)
    {
        RequestDelegate next = _ => throw new InvalidOperationException("Intentional fault for reporting test.");
        return new ExceptionHandlingMiddleware(next, archive, NullLogger<ExceptionHandlingMiddleware>.Instance);
    }

    private static DefaultHttpContext BuildContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/tools/network-storage/ec13d753a7ea4d66";
        context.Response.Body = new MemoryStream();
        context.RequestAborted = new CancellationTokenSource().Token;
        context.Items[CorrelationContext.ItemKey] = "req_test_correlation";
        return context;
    }

    private sealed class TokenRecordingArchive : IErrorArchive
    {
        public bool WasCalled { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool ThrowIfCancelled { get; init; }

        public Task<CapturedErrorDto> CaptureAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            WasCalled = true;
            if (ThrowIfCancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

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

    private sealed class TokenRecordingAlertSink : IExceptionAlertSink
    {
        public bool WasCalled { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            WasCalled = true;
            return Task.CompletedTask;
        }
    }
}
