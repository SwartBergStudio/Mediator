namespace Mediator.Core;

/// <summary>
/// Facade that implements <see cref="IMediator"/> by delegating to the specialized dispatchers
/// (<see cref="IRequestDispatcher"/>, <see cref="ICommandDispatcher"/>, <see cref="INotificationPublisher"/>,
/// <see cref="IStreamRequestDispatcher"/>).
/// </summary>
/// <remarks>
/// Dispatchers are resolved from the scope on first use rather than in the constructor, so a scope that only sends one
/// request creates one dispatcher. Replacing a dispatcher registration in DI still takes effect.
/// </remarks>
internal sealed class Mediator(IServiceProvider serviceProvider) : IMediator
{
    private IRequestDispatcher? _requestDispatcher;
    private ICommandDispatcher? _commandDispatcher;
    private INotificationPublisher? _notificationPublisher;
    private IStreamRequestDispatcher? _streamRequestDispatcher;

    /// <summary>
    /// Sends a request and awaits a response.
    /// </summary>
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => (_requestDispatcher ??= serviceProvider.GetRequiredService<IRequestDispatcher>()).Send(request, cancellationToken);

    /// <summary>
    /// Sends a generic command without response.
    /// </summary>
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
        => (_commandDispatcher ??= serviceProvider.GetRequiredService<ICommandDispatcher>()).Send(request, cancellationToken);

    /// <summary>
    /// Sends a non-generic command without response.
    /// </summary>
    public Task Send(IRequest request, CancellationToken cancellationToken = default)
        => (_commandDispatcher ??= serviceProvider.GetRequiredService<ICommandDispatcher>()).Send(request, cancellationToken);

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
        => (_streamRequestDispatcher ??= serviceProvider.GetRequiredService<IStreamRequestDispatcher>()).CreateStream(request, cancellationToken);
}
