namespace Mediator
{
    /// <summary>
    /// Defines a handler for a request with response.
    /// </summary>
    public interface IRequestHandler<in TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        /// <summary>
        /// Handle the request and return a response.
        /// </summary>
        Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Defines a handler for a request without response.
    /// </summary>
    public interface IRequestHandler<in TRequest>
        where TRequest : IRequest
    {
        /// <summary>
        /// Handle the request.
        /// </summary>
        Task Handle(TRequest request, CancellationToken cancellationToken);
    }
}
