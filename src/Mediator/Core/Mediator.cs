namespace Mediator.Core;

/// <summary>
/// Default <see cref="IMediator"/>. Requests, commands and streams are dispatched directly to the cached,
/// strongly-typed pipeline for the message type, resolving handlers from the caller's DI scope; notifications are
/// handed to the background <see cref="INotificationPublisher"/>.
/// </summary>
internal sealed class Mediator(IServiceProvider serviceProvider) : IMediator
{
    private INotificationPublisher? _notificationPublisher;

    /// <summary>
    /// Sends a request and awaits a response.
    /// </summary>
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => Dispatch.Send(request, serviceProvider, cancellationToken);

    /// <summary>
    /// Sends a generic command without response.
    /// </summary>
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
        => Dispatch.SendCommand(request, serviceProvider, cancellationToken);

    /// <summary>
    /// Sends a non-generic command without response.
    /// </summary>
    public Task Send(IRequest request, CancellationToken cancellationToken = default)
        => Dispatch.SendCommand(request, serviceProvider, cancellationToken);

    /// <summary>
    /// Publishes a notification for background processing.
    /// </summary>
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
        => (_notificationPublisher ??= serviceProvider.GetRequiredService<INotificationPublisher>()).Publish(notification, cancellationToken);

    /// <summary>
    /// Sends a streaming request and returns an async enumerable of response items.
    /// </summary>
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
        => Dispatch.CreateStream(request, serviceProvider, cancellationToken);
}
