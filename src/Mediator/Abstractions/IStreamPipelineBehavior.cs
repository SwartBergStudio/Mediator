namespace Mediator
{
    /// <summary>
    /// Pipeline behavior for streaming requests. Wraps the stream handler
    /// to add cross-cutting concerns such as logging, metrics, or validation.
    /// </summary>
    /// <typeparam name="TRequest">The streaming request type.</typeparam>
    /// <typeparam name="TResponse">The type of each streamed response item.</typeparam>
    public interface IStreamPipelineBehavior<in TRequest, TResponse>
        where TRequest : IStreamRequest<TResponse>
    {
        /// <summary>
        /// Handle the streaming request and call the next behavior in the pipeline.
        /// </summary>
        IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
    }
}
