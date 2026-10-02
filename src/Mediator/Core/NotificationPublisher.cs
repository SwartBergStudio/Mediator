using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Mediator.Core.Wrappers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// Publishes notifications for background processing with optional persistence.
/// Manages notification channel and background notification processing.
/// </summary>
/// <remarks>
/// With persistence enabled, a notification is persisted before it is queued and completed (removed) only after
/// every handler succeeded. If a handler fails the persisted item is scheduled for retry with exponential backoff,
/// up to <see cref="MediatorOptions.MaxRetryAttempts"/>. Items still queued in this process are never re-queued by
/// the recovery loop, so a notification is handled once unless a handler fails or the process stops.
/// </remarks>
internal sealed class NotificationPublisher : INotificationPublisher, IDisposable
{
    private static readonly TimeSpan s_shutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly IScopeProvider _scopeProvider;
    private readonly ILogger<NotificationPublisher> _logger;
    private readonly MediatorOptions _options;
    private readonly INotificationPersistence? _persistence;
    private readonly INotificationSerializer? _serializer;
    private readonly bool _isDebugEnabled;
    private readonly bool _continueOnCapturedContext;

    private readonly Channel<QueuedNotification> _channel;
    private readonly ConcurrentDictionary<string, byte> _inFlightPersistedIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly Task[] _maintenanceLoops;
    private readonly TimeSpan[] _retryDelays;

    private bool _disposed;

    public NotificationPublisher(
        IServiceProvider serviceProvider,
        IScopeProvider scopeProvider,
        ILogger<NotificationPublisher> logger,
        IOptions<MediatorOptions> options)
    {
        _scopeProvider = scopeProvider;
        _logger = logger;
        _options = options.Value;
        _isDebugEnabled = _logger.IsEnabled(LogLevel.Debug);
        SanitizeOptions(_options);
        _continueOnCapturedContext = !_options.UseConfigureAwaitGlobally;

        if (_options.EnablePersistence)
        {
            _persistence = serviceProvider.GetService<INotificationPersistence>();
            _serializer = serviceProvider.GetService<INotificationSerializer>();

            if (_persistence == null || _serializer == null)
            {
                _logger.LogWarning("Persistence enabled but required services (INotificationPersistence, INotificationSerializer) are not registered. Disabling persistence.");
                _options.EnablePersistence = false;
                _persistence = null;
                _serializer = null;
            }
        }

        _logger.LogInformation("Initializing NotificationPublisher with EnablePersistence={EnablePersistence}, WorkerCount={WorkerCount}, ChannelCapacity={ChannelCapacity}",
            _options.EnablePersistence, _options.NotificationWorkerCount, _options.ChannelCapacity);

        _retryDelays = BuildRetryDelays(_options);

        _channel = Channel.CreateBounded<QueuedNotification>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _options.NotificationWorkerCount == 1,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _workers = new Task[Math.Max(0, _options.NotificationWorkerCount)];
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i] = Task.Run(ProcessNotificationsAsync, _shutdown.Token);
        }
        _logger.LogInformation("Started {WorkerCount} background notification workers", _workers.Length);

        if (_persistence != null)
        {
            _maintenanceLoops = new[]
            {
                Task.Run(() => RunPeriodicAsync(_options.ProcessingInterval, RecoverNotificationsAsync), _shutdown.Token),
                Task.Run(() => RunPeriodicAsync(_options.CleanupInterval, CleanupAsync), _shutdown.Token),
            };
            _logger.LogInformation("Started recovery and cleanup loops with ProcessingInterval={ProcessingInterval}, CleanupInterval={CleanupInterval}",
                _options.ProcessingInterval, _options.CleanupInterval);
        }
        else
        {
            _maintenanceLoops = Array.Empty<Task>();
        }
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        var notificationType = notification?.GetType() ?? typeof(TNotification);
        if (_isDebugEnabled) _logger.LogDebug("Publishing notification of type {NotificationType}", notificationType.Name);

        var workItem = new NotificationWorkItem(notification, notificationType, DateTime.UtcNow, string.Empty);
        string? persistenceId = null;

        if (_persistence != null)
        {
            (workItem, persistenceId) = await TryPersistAsync(workItem, cancellationToken).ConfigureAwait(_continueOnCapturedContext);
        }

        try
        {
            var queued = new QueuedNotification(workItem, persistenceId, AttemptCount: 0);
            if (_channel.Writer.TryWrite(queued))
            {
                if (_isDebugEnabled) _logger.LogDebug("Notification {NotificationType} written to channel (TryWrite succeeded)", notificationType.Name);
                return;
            }

            if (_isDebugEnabled) _logger.LogDebug("Channel full for {NotificationType}, waiting for space", notificationType.Name);
            await _channel.Writer.WriteAsync(queued, cancellationToken).ConfigureAwait(_continueOnCapturedContext);
            if (_isDebugEnabled) _logger.LogDebug("Notification {NotificationType} written to channel", notificationType.Name);
        }
        catch (Exception ex)
        {
            // The persisted copy (if any) stays on disk and will be picked up by the recovery loop.
            if (persistenceId != null) _inFlightPersistedIds.TryRemove(persistenceId, out _);
            _logger.LogError(ex, "Failed to write notification {NotificationType} to channel", notificationType.Name);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shutdown.Cancel();
        _channel.Writer.TryComplete();

        WaitQuietly(_workers);
        WaitQuietly(_maintenanceLoops);

        _shutdown.Dispose();
    }

    private async Task<(NotificationWorkItem WorkItem, string? PersistenceId)> TryPersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken)
    {
        var notificationType = workItem.NotificationType!;
        try
        {
            var serialized = _serializer!.Serialize(workItem.Notification, notificationType) ?? string.Empty;
            workItem = new NotificationWorkItem(workItem.Notification, notificationType, workItem.CreatedAt, serialized);
            if (serialized.Length == 0)
                return (workItem, null);

            if (_isDebugEnabled) _logger.LogDebug("Persisting notification {NotificationType}", notificationType.Name);
            var id = await _persistence!.PersistAsync(workItem, cancellationToken).ConfigureAwait(_continueOnCapturedContext);
            if (_isDebugEnabled) _logger.LogDebug("Notification {NotificationType} persisted successfully", notificationType.Name);

            if (string.IsNullOrEmpty(id))
                return (workItem, null);

            // Mark as in-flight before queueing so the recovery loop never re-queues it concurrently.
            _inFlightPersistedIds.TryAdd(id, 0);
            return (workItem, id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist notification {NotificationType}, processing in-memory only", notificationType.Name);
            return (workItem, null);
        }
    }

    private static void SanitizeOptions(MediatorOptions o)
    {
        if (o.ChannelCapacity <= 0) o.ChannelCapacity = 1;
        if (o.ProcessingBatchSize <= 0) o.ProcessingBatchSize = 1;
        if (o.MaxRetryAttempts < 0) o.MaxRetryAttempts = 0;
        if (o.InitialRetryDelay < TimeSpan.Zero) o.InitialRetryDelay = TimeSpan.Zero;
        if (o.RetryDelayMultiplier <= 0) o.RetryDelayMultiplier = 1.0;
        if (o.CleanupInterval <= TimeSpan.Zero) o.CleanupInterval = TimeSpan.FromMinutes(1);
        if (o.ProcessingInterval <= TimeSpan.Zero) o.ProcessingInterval = TimeSpan.FromSeconds(1);
    }

    private static TimeSpan[] BuildRetryDelays(MediatorOptions options)
    {
        if (options.MaxRetryAttempts <= 0)
            return Array.Empty<TimeSpan>();

        var delays = new TimeSpan[options.MaxRetryAttempts];
        for (var i = 0; i < delays.Length; i++)
        {
            delays[i] = TimeSpan.FromTicks((long)(options.InitialRetryDelay.Ticks * Math.Pow(options.RetryDelayMultiplier, i)));
        }
        return delays;
    }

    private async Task RunPeriodicAsync(TimeSpan interval, Func<Task> action)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await action().ConfigureAwait(false);
                }
                catch (Exception ex) when (!_shutdown.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Periodic task error occurred");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessNotificationsAsync()
    {
        _logger.LogInformation("Background notification processor started");
        try
        {
            await foreach (var queued in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessQueuedAsync(queued).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception while processing notification {NotificationType}", queued.WorkItem.NotificationType?.Name ?? "null");
                }
                finally
                {
                    if (queued.PersistenceId != null) _inFlightPersistedIds.TryRemove(queued.PersistenceId, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Background notification processor cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Background notification processor encountered critical error");
        }
        finally
        {
            _logger.LogInformation("Background notification processor stopped");
        }
    }

    private async Task ProcessQueuedAsync(QueuedNotification queued)
    {
        var workItem = queued.WorkItem;
        Exception? failure = null;

        if (workItem.NotificationType == null || workItem.Notification == null)
        {
            _logger.LogError("Received invalid notification work item, skipping. NotificationType={NotificationType}, Notification={Notification}",
                workItem.NotificationType?.Name ?? "null",
                workItem.Notification?.GetType().Name ?? "null");
        }
        else
        {
            failure = await DispatchToHandlersAsync(workItem.NotificationType, workItem.Notification).ConfigureAwait(false);
        }

        if (queued.PersistenceId != null)
        {
            await SettlePersistedAsync(queued.PersistenceId, queued.AttemptCount, failure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Invokes every handler for the notification in a dedicated DI scope. Handler failures are isolated and logged.
    /// </summary>
    /// <returns><c>null</c> when all handlers succeeded; otherwise the handler exception(s).</returns>
    private async Task<Exception?> DispatchToHandlersAsync(Type notificationType, object notification)
    {
        if (_isDebugEnabled) _logger.LogDebug("Processing notification {NotificationType}", notificationType.Name);

        var wrapper = HandlerWrapperCache.GetNotificationWrapper(notificationType);
        if (wrapper == null)
        {
            _logger.LogWarning("No handler wrapper registered for notification type {NotificationType}; it will not be handled. Register handlers with the source generator when running with Native AOT.", notificationType.Name);
            return null;
        }

        using var scope = _scopeProvider.CreateScope();
        var handlers = wrapper.ResolveHandlers(scope.ServiceProvider);
        if (_isDebugEnabled) _logger.LogDebug("Discovered {HandlerCount} handlers for notification type {NotificationType}", handlers.Length, notificationType.Name);

        if (handlers.Length == 0)
            return null;

        var token = _shutdown.Token;
        if (handlers.Length == 1)
            return await InvokeHandlerAsync(wrapper, handlers[0], notification, token).ConfigureAwait(false);

        var count = handlers.Length;
        var pool = ArrayPool<Task<Exception?>>.Shared;
        var tasks = pool.Rent(count);
        try
        {
            // Start every handler before awaiting so they run concurrently.
            for (var i = 0; i < count; i++)
            {
                tasks[i] = InvokeHandlerAsync(wrapper, handlers[i], notification, token);
            }

            List<Exception>? failures = null;
            for (var i = 0; i < count; i++)
            {
                var failure = await tasks[i].ConfigureAwait(false);
                if (failure != null) (failures ??= new List<Exception>()).Add(failure);
            }

            if (_isDebugEnabled) _logger.LogDebug("All {HandlerCount} handlers completed for {NotificationType}", count, notificationType.Name);
            return failures == null ? null : failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
        finally
        {
            Array.Clear(tasks, 0, count);
            pool.Return(tasks);
        }
    }

    private async Task<Exception?> InvokeHandlerAsync(NotificationHandlerWrapper wrapper, object handler, object notification, CancellationToken token)
    {
        try
        {
            if (_isDebugEnabled) _logger.LogDebug("Calling Handle method on {HandlerType}", handler.GetType().Name);
            await wrapper.Handle(handler, notification, token).ConfigureAwait(false);
            if (_isDebugEnabled) _logger.LogDebug("Handler {HandlerType} completed successfully", handler.GetType().Name);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification handler {HandlerType} failed with exception", handler.GetType().Name);
            return ex;
        }
    }

    private async Task SettlePersistedAsync(string id, int attemptCount, Exception? failure)
    {
        try
        {
            if (failure == null)
            {
                await _persistence!.CompleteAsync(id, _shutdown.Token).ConfigureAwait(false);
                if (_isDebugEnabled) _logger.LogDebug("Marked persisted notification {NotificationId} as complete", id);
            }
            else
            {
                await ScheduleRetryAsync(id, attemptCount, failure).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Shutting down: the persisted item stays on disk and is recovered on the next start.
        }
    }

    private async Task RecoverNotificationsAsync()
    {
        if (_isDebugEnabled) _logger.LogDebug("Starting recovery of pending notifications");
        var pending = await _persistence!.GetPendingAsync(_options.ProcessingBatchSize, _shutdown.Token).ConfigureAwait(false);

        foreach (var persistedItem in pending)
        {
            if (!IsValidPersistedItem(persistedItem))
            {
                _logger.LogWarning("Invalid persisted item found during recovery, skipping. Id: {Id}", persistedItem?.Id ?? "null");
                continue;
            }

            // Already queued in this process (published or recovered earlier and not yet processed).
            if (!_inFlightPersistedIds.TryAdd(persistedItem.Id, 0))
                continue;

            try
            {
                if (_isDebugEnabled) _logger.LogDebug("Processing recovered notification {NotificationId}", persistedItem.Id);
                RequeuePersistedItem(persistedItem);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
            {
                _inFlightPersistedIds.TryRemove(persistedItem.Id, out _);
                _logger.LogError(ex, "Failed to recover notification {NotificationId}", persistedItem.Id);
                await ScheduleRetryAsync(persistedItem.Id, persistedItem.AttemptCount, ex).ConfigureAwait(false);
            }
        }
    }

    private static bool IsValidPersistedItem([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] PersistedNotificationWorkItem? item)
    {
        return item != null &&
               !string.IsNullOrEmpty(item.Id) &&
               item.WorkItem.NotificationType != null &&
               !string.IsNullOrEmpty(item.WorkItem.SerializedNotification);
    }

    private void RequeuePersistedItem(PersistedNotificationWorkItem persistedItem)
    {
        var source = persistedItem.WorkItem;
        var notification = _serializer!.Deserialize(source.SerializedNotification, source.NotificationType!)
            ?? throw new InvalidOperationException($"Deserialization returned null for persisted notification {persistedItem.Id}");

        var workItem = new NotificationWorkItem(notification, source.NotificationType, source.CreatedAt, source.SerializedNotification);
        var queued = new QueuedNotification(workItem, persistedItem.Id, persistedItem.AttemptCount);

        if (!_channel.Writer.TryWrite(queued))
        {
            _inFlightPersistedIds.TryRemove(persistedItem.Id, out _);
            _logger.LogWarning("Channel full while re-queueing persisted notification {NotificationId}, will retry on the next recovery pass", persistedItem.Id);
            return;
        }

        if (_isDebugEnabled) _logger.LogDebug("Re-queued persisted notification {NotificationId} to channel", persistedItem.Id);
    }

    private async Task ScheduleRetryAsync(string id, int attemptCount, Exception failure)
    {
        if (attemptCount >= _options.MaxRetryAttempts)
        {
            _logger.LogWarning(failure, "Max retry attempts ({MaxRetries}) reached for notification {NotificationId}, giving up", _options.MaxRetryAttempts, id);
            await _persistence!.CompleteAsync(id, _shutdown.Token).ConfigureAwait(false);
            return;
        }

        var delay = _retryDelays[Math.Max(0, attemptCount)];
        var retryAfter = DateTime.UtcNow.Add(delay);
        _logger.LogInformation("Scheduling retry for persisted notification {NotificationId}, attempt {AttemptCount}, retry after {RetryAfter}", id, attemptCount + 1, retryAfter);

        await _persistence!.FailAsync(id, failure, retryAfter, _shutdown.Token).ConfigureAwait(false);
    }

    private async Task CleanupAsync()
    {
        var cutoffDate = DateTime.UtcNow.Subtract(_options.CleanupRetentionPeriod);
        if (_isDebugEnabled) _logger.LogDebug("Running cleanup of persisted notifications before {CutoffDate}", cutoffDate);
        await _persistence!.CleanupAsync(cutoffDate, _shutdown.Token).ConfigureAwait(false);
        _logger.LogInformation("Cleanup completed for persisted notifications before {CutoffDate}", cutoffDate);
    }

    private static void WaitQuietly(Task[] tasks)
    {
        if (tasks.Length == 0) return;
        try { Task.WaitAll(tasks, s_shutdownTimeout); }
        catch (AggregateException) { }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// A notification queued for background processing, with its persistence id when it was persisted.
    /// </summary>
    private readonly record struct QueuedNotification(NotificationWorkItem WorkItem, string? PersistenceId, int AttemptCount);
}
