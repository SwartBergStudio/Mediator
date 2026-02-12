namespace Mediator
{
    /// <summary>
    /// Represents work to be processed by notification handlers.
    /// </summary>
    public readonly struct NotificationWorkItem
    {
        /// <summary>
        /// The notification object to be processed.
        /// </summary>
        public object? Notification { get; init; }

        /// <summary>
        /// Type of the notification for handler resolution.
        /// </summary>
        public Type? NotificationType { get; init; }

        /// <summary>
        /// When this work item was created.
        /// </summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>
        /// Serialized representation for persistence.
        /// </summary>
        public string SerializedNotification { get; init; }

        /// <summary>
        /// Initializes a new instance of the <see cref="NotificationWorkItem"/> struct.
        /// </summary>
        /// <param name="notification">The notification instance.</param>
        /// <param name="notificationType">The CLR type of the notification.</param>
        /// <param name="createdAt">Creation timestamp (UTC).</param>
        /// <param name="serializedNotification">Serialized notification payload.</param>
        public NotificationWorkItem(object? notification, Type? notificationType, DateTime createdAt, string serializedNotification)
        {
            Notification = notification;
            NotificationType = notificationType;
            CreatedAt = createdAt;
            SerializedNotification = serializedNotification;
        }
    }

}