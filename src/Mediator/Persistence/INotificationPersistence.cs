namespace Mediator.Persistence
{
    /// <summary>
    /// Interface for persisting notifications for crash recovery.
    /// </summary>
    public interface INotificationPersistence : IDisposable
    {
        /// <summary>
        /// Persist a notification work item.
        /// </summary>
        Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get pending notification work items.
        /// </summary>
        Task<IEnumerable<PersistedNotificationWorkItem>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default);

        /// <summary>
        /// Mark a notification as successfully processed.
        /// </summary>
        Task CompleteAsync(string id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Mark a notification as failed with retry information.
        /// </summary>
        Task FailAsync(string id, Exception exception, DateTime? retryAfter = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Clean up old notification records.
        /// </summary>
        Task CleanupAsync(DateTime olderThan, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents a persisted notification work item with metadata.
    /// </summary>
    public class PersistedNotificationWorkItem
    {
        /// <summary>
        /// Unique identifier for the persisted item.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// The original work item.
        /// </summary>
        public NotificationWorkItem WorkItem { get; set; }

        /// <summary>
        /// When the item was first persisted.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When to retry processing (null for immediate).
        /// </summary>
        public DateTime? RetryAfter { get; set; }

        /// <summary>
        /// Number of processing attempts.
        /// </summary>
        public int AttemptCount { get; set; }

        /// <summary>
        /// Last exception encountered.
        /// </summary>
        public Exception? LastException { get; set; }
    }
}
namespace Mediator.Persistence
{
    /// <summary>
    /// Optional extension for <see cref="INotificationPersistence"/>: stores a retry item together with its attempt
    /// count and retry time in one step.
    /// </summary>
    /// <remarks>
    /// When some handlers of a notification fail, the mediator stores one retry item per failed handler. With this
    /// interface the item is never visible as "ready" before its retry time. Without it, the mediator falls back to
    /// <see cref="INotificationPersistence.PersistAsync"/> followed by <see cref="INotificationPersistence.FailAsync"/>
    /// and pauses its own recovery loop in between; other processes sharing the same store could still see the item
    /// in that short window. Implement this in stores shared by several app instances.
    /// </remarks>
    public interface INotificationRetryPersistence
    {
        /// <summary>
        /// Persists a work item that should first run at <paramref name="retryAfter"/>, with the given attempt count.
        /// </summary>
        /// <returns>The id of the stored item.</returns>
        Task<string> PersistForRetryAsync(NotificationWorkItem workItem, int attemptCount, DateTime retryAfter, Exception? exception, CancellationToken cancellationToken = default);
    }
}
