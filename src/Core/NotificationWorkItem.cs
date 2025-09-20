namespace Mediator
{
    /// <summary>
    /// Represents work to be processed by notification handlers.
    /// </summary>
    public struct NotificationWorkItem
    {
        /// <summary>
        /// The notification object to be processed.
        /// </summary>
        public object? Notification { get; set; }

        /// <summary>
        /// Type of the notification for handler resolution.
        /// </summary>
        public Type? NotificationType { get; set; }

        /// <summary>
        /// When this work item was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Serialized representation for persistence.
        /// </summary>
        public string SerializedNotification { get; set; }
    }

    /// <summary>
    /// Represents a persisted notification with retry metadata.
    /// </summary>
    public class PersistedNotificationWorkItem
    {
        /// <summary>
        /// Unique identifier for this persisted item.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// The notification work item data.
        /// </summary>
        public NotificationWorkItem WorkItem { get; set; }

        /// <summary>
        /// When this item was first created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Number of processing attempts made.
        /// </summary>
        public int AttemptCount { get; set; }

        /// <summary>
        /// When to retry processing (null if not scheduled for retry).
        /// </summary>
        public DateTime? RetryAfter { get; set; }

        /// <summary>
        /// Whether this item has been completed successfully.
        /// </summary>
        public bool IsCompleted { get; set; }

        /// <summary>
        /// Last error message if processing failed.
        /// </summary>
        public string? LastError { get; set; }
    }
}