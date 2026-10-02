namespace Mediator.Core.Wrappers;

/// <summary>
/// Strongly-typed entry point for a notification type. Resolves the handlers for the notification
/// and invokes them without reflection.
/// </summary>
internal abstract class NotificationHandlerWrapper
{
    /// <summary>
    /// Resolves all handlers for the notification type. The returned array is covariant, so no copy is made.
    /// </summary>
    public abstract object[] ResolveHandlers(IServiceProvider serviceProvider);

    public abstract Task Handle(object handler, object notification, CancellationToken cancellationToken);
}

internal sealed class NotificationHandlerWrapper<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    public override object[] ResolveHandlers(IServiceProvider serviceProvider)
        => HandlerResolution.ResolveAll<INotificationHandler<TNotification>>(serviceProvider);

    public override Task Handle(object handler, object notification, CancellationToken cancellationToken)
        => ((INotificationHandler<TNotification>)handler).Handle((TNotification)notification, cancellationToken);
}
