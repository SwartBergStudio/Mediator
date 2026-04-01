namespace Mediator
{
    /// <summary>
    /// Delegate for handling streaming requests in the pipeline.
    /// </summary>
    public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<out TResponse>();
}
