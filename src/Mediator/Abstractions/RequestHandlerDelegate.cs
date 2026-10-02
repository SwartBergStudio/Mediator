namespace Mediator
{
    /// <summary>
    /// Delegate for handling requests in the pipeline.
    /// </summary>
    public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

    /// <summary>
    /// Delegate for handling requests without a response in the pipeline.
    /// </summary>
    public delegate Task RequestHandlerDelegate();
}
