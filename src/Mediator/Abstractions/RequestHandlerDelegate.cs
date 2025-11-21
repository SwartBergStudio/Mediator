namespace Mediator
{
    /// <summary>
    /// Delegate for handling requests in the pipeline.
    /// </summary>
    public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();
}
