namespace Mediator;

/// <summary>
/// Dispatches commands without expecting responses.
/// Encapsulates command handling logic for Send operations.
/// </summary>
public interface ICommandDispatcher
{
    /// <summary>
    /// Sends a command without expecting a response.
    /// </summary>
    /// <typeparam name="TRequest">Request type.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest;

    /// <summary>
    /// Sends a non-generic command without expecting a response.
    /// </summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Send(IRequest request, CancellationToken cancellationToken = default);
}
