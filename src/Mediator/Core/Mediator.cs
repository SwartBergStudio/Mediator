namespace Mediator.Core;

/// <summary>
/// Composite facade that implements IMediator by delegating to specialized handlers.
/// This is the main entry point for the mediat pattern, delegating Send and Publish operations
/// to their respective specialized handlers (RequestDispatcher, CommandDispatcher, NotificationPublisher).
/// </summary>
internal sealed class Mediator : IMediator
{
    private readonly IRequestDispatcher _requestDispatcher;
    private readonly ICommandDispatcher _commandDispatcher;
    private readonly INotificationPublisher _notificationPublisher;

    /// <summary>
    /// Initializes a new instance of the CompositeMediator with specialized handlers.
    /// </summary>
    /// <param name="requestDispatcher">Handles Send&lt;TResponse&gt; operations.</param>
    /// <param name="commandDispatcher">Handles Send operations (commands).</param>
    /// <param name="notificationPublisher">Handles Publish operations.</param>
    public Mediator(
        IRequestDispatcher requestDispatcher,
        ICommandDispatcher commandDispatcher,
        INotificationPublisher notificationPublisher)
    {
        _requestDispatcher = requestDispatcher;
        _commandDispatcher = commandDispatcher;
        _notificationPublisher = notificationPublisher;
    }

    /// <summary>
    /// Sends a request and awaits a response.
    /// </summary>
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => _requestDispatcher.Send(request, cancellationToken);

    /// <summary>
    /// Sends a generic command without response.
    /// </summary>
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
        => _commandDispatcher.Send(request, cancellationToken);

    /// <summary>
    /// Sends a non-generic command without response.
    /// </summary>
    public Task Send(IRequest request, CancellationToken cancellationToken = default)
        => _commandDispatcher.Send(request, cancellationToken);

    /// <summary>
    /// Publishes a notification for background processing.
    /// </summary>
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
        => _notificationPublisher.Publish(notification, cancellationToken);
}
