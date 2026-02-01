namespace Mediator;

/// <summary>
/// Dispatches requests and returns responses with optional pipeline behaviors.
/// Encapsulates request handling logic for Send&lt;TResponse&gt; operations.
/// </summary>
public interface IRequestDispatcher
{
    /// <summary>
    /// Sends a request and awaits a response asynchronously.
    /// </summary>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response produced by the handler.</returns>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
