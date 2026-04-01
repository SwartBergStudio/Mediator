namespace Mediator
{
    /// <summary>
    /// Marker interface for streaming requests that yield multiple response items.
    /// </summary>
    /// <typeparam name="TResponse">The type of each streamed response item.</typeparam>
    public interface IStreamRequest<out TResponse>
    {
    }
}
