using Azure;
using Azure.Core;
using Azure.Core.Pipeline;

namespace AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;

/// <summary>
/// Creates the cache container the first time a request finds it missing, then retries the request.
/// </summary>
/// <remarks>
/// Creating the container lazily, off the error response, costs nothing on the overwhelmingly common
/// path where it already exists - unlike an eager create on construction, which would add a request
/// to every cold start and would need create permission even when the container is provisioned out
/// of band.
/// </remarks>
internal sealed class ContainerCreatingRetryPolicy : RetryPolicy
{
    /// <summary>
    /// Handlers keyed by the storage error code that triggers them.
    /// </summary>
    public Dictionary<string, Func<CancellationToken, Task>> ErrorHandlers { get; } = [];

    /// <inheritdoc />
    protected override async ValueTask<bool> ShouldRetryAsync(HttpMessage message, Exception? exception)
    {
        if (await this.TryHandleAsync(message, exception, message.CancellationToken))
        {
            return true;
        }

        return await base.ShouldRetryAsync(message, exception);
    }

    /// <inheritdoc />
    protected override bool ShouldRetry(HttpMessage message, Exception? exception)
    {
        // The synchronous cache members call the synchronous SDK overloads, which never reach
        // ShouldRetryAsync. Without this the container would be created only by whichever call
        // happened to be asynchronous.
        if (this.TryHandleAsync(message, exception, message.CancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult())
        {
            return true;
        }

        return base.ShouldRetry(message, exception);
    }

    private async ValueTask<bool> TryHandleAsync(
        HttpMessage message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        exception ??= message.HasResponse ? new RequestFailedException(message.Response) : null;

        // ErrorCode is null for a transport-level failure, where there is no service response to
        // read a code from. Indexing a dictionary with it would throw and replace a retryable
        // network error with an ArgumentNullException.
        if (exception is not RequestFailedException requestException ||
            requestException.ErrorCode is not { } errorCode ||
            !this.ErrorHandlers.TryGetValue(errorCode, out var handler))
        {
            return false;
        }

        await handler(cancellationToken);

        return true;
    }
}
