namespace Mediator
{
    /// <summary>
    /// Marker interface for requests with response.
    /// </summary>
    public interface IRequest<out TResponse>
    {
    }

    /// <summary>
    /// Marker interface for requests without response.
    /// </summary>
    public interface IRequest
    {
    }
}
