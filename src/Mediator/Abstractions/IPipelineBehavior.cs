namespace Mediator
{
    /// <summary>
    /// Pipeline behavior for requests that return a response (<see cref="IRequest{TResponse}"/>).
    /// </summary>
    public interface IPipelineBehavior<in TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        /// <summary>
        /// Handle the request and call the next behavior in the pipeline.
        /// </summary>
        Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Pipeline behavior for requests without a response (<see cref="IRequest"/>), such as commands.
    /// </summary>
    /// <remarks>
    /// Works like <see cref="IPipelineBehavior{TRequest, TResponse}"/>: behaviors run in registration order around the
    /// handler, and can be registered manually or declared with <see cref="MediatorPipelineBehaviorsAttribute"/>.
    /// </remarks>
    public interface IPipelineBehavior<in TRequest>
        where TRequest : IRequest
    {
        /// <summary>
        /// Handle the request and call the next behavior in the pipeline.
        /// </summary>
        Task Handle(TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken);
    }
}
