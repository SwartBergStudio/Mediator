using System.Collections.Generic;

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