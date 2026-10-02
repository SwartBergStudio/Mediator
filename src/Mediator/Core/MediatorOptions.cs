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
        /// When disabled, no persistence or serialization services are registered or initialized.
        /// </summary>
        public bool EnablePersistence { get; set; } = false;

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

        /// <summary>
        /// Global setting to use ConfigureAwait(false) for the mediator's own awaits (notification publishing and
        /// persistence). Requests, commands and streams are not awaited by the mediator at all, so your code's own
        /// await (with or without ConfigureAwait) decides where its continuation runs.
        /// When true (default), all handlers will use ConfigureAwait(false) for optimal performance and safety.
        /// When false, handlers will use normal task behavior which can cause deadlocks in UI applications.
        /// </summary>
        public bool UseConfigureAwaitGlobally { get; set; } = true;
    }
}