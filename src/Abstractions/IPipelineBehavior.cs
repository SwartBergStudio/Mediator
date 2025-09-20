namespace Mediator
{
    /// <summary>
    /// Pipeline behavior for requests.
    /// </summary>
    public interface IPipelineBehavior<in TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        /// <summary>
        /// Handle the request and call the next behavior in the pipeline.
        /// </summary>
        Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
    }
}
