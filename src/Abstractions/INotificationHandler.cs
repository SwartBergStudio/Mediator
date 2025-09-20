namespace Mediator
{
    /// <summary>
    /// Defines a handler for a notification.
    /// </summary>
    public interface INotificationHandler<in TNotification>
        where TNotification : INotification
    {
        /// <summary>
        /// Handle the notification.
        /// </summary>
        Task Handle(TNotification notification, CancellationToken cancellationToken);
    }
}
