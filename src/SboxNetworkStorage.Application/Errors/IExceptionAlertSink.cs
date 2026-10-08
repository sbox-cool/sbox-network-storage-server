using SboxNetworkStorage.Contracts.Errors;

namespace SboxNetworkStorage.Application.Errors;

public interface IExceptionAlertSink
{
    /// <summary>
    /// Sends the captured error to an external alerting channel (e.g. Discord).
    /// Implementations SHOULD also ensure the error is persisted in the
    /// <see cref="IErrorArchive"/> so that it appears on <c>/admin/errors</c>,
    /// even when the caller's own archive attempt failed.
    /// </summary>
    Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken);
}

public sealed class NoopExceptionAlertSink : IExceptionAlertSink
{
    public Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken) => Task.CompletedTask;
}
