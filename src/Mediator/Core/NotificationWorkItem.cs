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
        /// The single handler this item is for, as <c>"Namespace.TypeName, AssemblyName"</c> (no version, so it still
        /// matches after the app is upgraded), or <c>null</c> for all handlers.
        /// </summary>
        /// <remarks>
        /// Set when a persisted notification is retried after some of its handlers failed: each failed handler gets its
        /// own item so handlers that already succeeded do not run again. Custom <see cref="Persistence.INotificationPersistence"/>
        /// implementations should store and return it; if they don't, a retry runs all handlers again (the earlier behavior).
        /// </remarks>
        public string? TargetHandlerType { get; init; }

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