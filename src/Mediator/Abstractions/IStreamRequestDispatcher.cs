namespace Mediator;

/// <summary>
/// Dispatches streaming requests and returns an async enumerable of response items.
/// Encapsulates stream request handling logic for CreateStream operations.
/// </summary>
public interface IStreamRequestDispatcher
{
    /// <summary>
    /// Sends a streaming request and returns an async enumerable of response items.
    /// </summary>
    /// <typeparam name="TResponse">The type of each streamed response item.</typeparam>
    /// <param name="request">The streaming request instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of response items produced by the handler.</returns>
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default);
}
