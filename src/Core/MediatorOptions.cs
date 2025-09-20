namespace Mediator.Core
{
    /// <summary>
    /// Configuration options for the Mediator.
    /// </summary>
    public class MediatorOptions
    {
        /// <summary>
        /// Number of background workers processing notifications.
        /// </summary>
        public int NotificationWorkerCount { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// Maximum capacity of the notification channel.
        /// </summary>
        public int ChannelCapacity { get; set; } = 1000;

        /// <summary>
        /// Whether to enable persistent storage for notifications.
        /// </summary>
        public bool EnablePersistence { get; set; } = true;

        /// <summary>
        /// Interval for processing persisted notifications.
        /// </summary>
        public TimeSpan ProcessingInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Number of notifications to process in each batch.
        /// </summary>
        public int ProcessingBatchSize { get; set; } = 100;

        /// <summary>
        /// Maximum number of retry attempts for failed notifications.
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 3;

        /// <summary>
        /// Initial delay before first retry attempt.
        /// </summary>
        public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Multiplier applied to retry delay for exponential backoff.
        /// </summary>
        public double RetryDelayMultiplier { get; set; } = 2.0;

        /// <summary>
        /// How long to keep completed notifications before cleanup.
        /// </summary>
        public TimeSpan CleanupRetentionPeriod { get; set; } = TimeSpan.FromDays(7);

        /// <summary>
        /// Interval for running cleanup operations.
        /// </summary>
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(6);
    }
}