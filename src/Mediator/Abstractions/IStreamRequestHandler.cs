namespace Mediator
{
    /// <summary>
    /// Defines a handler for a streaming request that yields multiple response items.
    /// </summary>
    /// <typeparam name="TRequest">The streaming request type.</typeparam>
    /// <typeparam name="TResponse">The type of each streamed response item.</typeparam>
    public interface IStreamRequestHandler<in TRequest, out TResponse>
        where TRequest : IStreamRequest<TResponse>
    {
        /// <summary>
        /// Handle the streaming request and yield response items.
        /// </summary>
        IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
    }
}
