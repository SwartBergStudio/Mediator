using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// Publishes notifications for background processing with optional persistence.
/// Manages notification channel and background notification processing.
/// </summary>
/// <remarks>
/// With persistence enabled, a notification is persisted before it is queued and completed (removed) only after
/// every handler succeeded. If handlers fail, each failed handler gets its own persisted retry item (see
/// <see cref="NotificationWorkItem.TargetHandlerType"/>), retried with exponential backoff up to
/// <see cref="MediatorOptions.MaxRetryAttempts"/>, so handlers that succeeded do not run again. Items still queued in
/// this process are never re-queued by the recovery loop, so a notification is handled once unless a handler fails or
/// the process stops.
/// </remarks>
internal sealed class NotificationPublisher : INotificationPublisher, IDisposable, IAsyncDisposable
{
    // How long shutdown waits for tasks to stop once cancelled (maintenance loops, or workers after ShutdownTimeout).
    private static readonly TimeSpan s_cancelGracePeriod = TimeSpan.FromSeconds(2);

    private readonly NotificationHandlerInvoker _invoker;
    private readonly ILogger<NotificationPublisher> _logger;
    private readonly MediatorOptions _options;
    private readonly INotificationPersistence? _persistence;
    private readonly INotificationSerializer? _serializer;
    private readonly bool _isDebugEnabled;
    private readonly bool _continueOnCapturedContext;

    private readonly Channel<QueuedNotification> _channel;
    private readonly ConcurrentDictionary<string, byte> _inFlightPersistedIds = new(StringComparer.Ordinal);

    // Persisted items this process finished (completed or rescheduled), with a sequence number. The recovery loop
    // works from a snapshot of pending items; an item finished after the snapshot was taken is stale in it and must not
    // be queued again. See RecoverPendingAsync.
    private readonly ConcurrentDictionary<string, long> _settledPersistedIds = new(StringComparer.Ordinal);
    private long _settleSequence;
    // Cancelled when shutdown stops waiting: the token handlers and persistence calls receive.
    private readonly CancellationTokenSource _shutdown = new();
    // Cancelled first at shutdown, so no recovery pass queues more work while the queue drains.
    private readonly CancellationTokenSource _stopMaintenance = new();
    // Cancelled to make workers stop taking queued notifications (they finish the one they are handling).
    private readonly CancellationTokenSource _stopReading = new();
    private readonly object _stopLock = new();
    private Task<bool>? _stopping;
    private readonly Task[] _workers;
    private readonly Task[] _maintenanceLoops;
    private readonly Func<int> _queueSize;

    // Serializes the recovery scan with the PersistAsync + FailAsync fallback for retry items, so the recovery loop can
    // never see a retry item before its retry time is set.
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);

    private int _disposed;

    public NotificationPublisher(
        IServiceProvider serviceProvider,
        IScopeProvider scopeProvider,
        ILogger<NotificationPublisher> logger,
        IOptions<MediatorOptions> options)
    {
        _invoker = new NotificationHandlerInvoker(scopeProvider, logger);
        _logger = logger;
        _options = options.Value;
        _isDebugEnabled = _logger.IsEnabled(LogLevel.Debug);
        SanitizeOptions(_options);
        if (_options.NotificationWorkerCount <= 0)
        {
            _logger.LogWarning("NotificationWorkerCount was {WorkerCount}; using 1. Without a worker, background notifications are never handled.", _options.NotificationWorkerCount);
            _options.NotificationWorkerCount = 1;
        }
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

        _channel = Channel.CreateBounded<QueuedNotification>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _options.NotificationWorkerCount == 1,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        // This singleton is created inside the first caller's request (its first Publish). The loops below live for the
        // life of the process, so they must not capture that request's ExecutionContext; otherwise every background
        // handler would see its AsyncLocal state (HttpContext, logger scopes, Activity.Current).
        using (ExecutionContext.SuppressFlow())
        {
            _workers = new Task[_options.NotificationWorkerCount];
            for (var i = 0; i < _workers.Length; i++)
            {
                _workers[i] = Task.Run(ProcessNotificationsAsync, _shutdown.Token);
            }
            _logger.LogInformation("Started {WorkerCount} background notification workers", _workers.Length);

            _queueSize = () => _channel.Reader.Count;
            MediatorTelemetry.RegisterQueue(_queueSize);

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
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        // Thrown inside the async method, so it surfaces through the task, like Send and PublishAndWait.
        if (notification is null)
            throw new ArgumentNullException(nameof(notification));

        var notificationType = notification.GetType();
        if (_isDebugEnabled) _logger.LogDebug("Publishing notification of type {NotificationType}", notificationType.Name);

        // The publish span covers persisting and queueing; background handler spans become its children, so a trace
        // shows the request that caused the work.
        MediatorTelemetry.RecordPublished(notificationType, "background");
        using var activity = MediatorTelemetry.StartActivity("publish", notificationType, kind: ActivityKind.Producer);
        activity?.SetTag(MediatorTelemetry.PublishModeTag, "background");
        var parent = Activity.Current?.Context ?? default;

        var workItem = new NotificationWorkItem(notification, notificationType, DateTime.UtcNow, string.Empty);
        string? persistenceId = null;

        if (_persistence != null)
        {
            bool claimed;
            (workItem, persistenceId, claimed) = await TryPersistAsync(workItem, cancellationToken).ConfigureAwait(_continueOnCapturedContext);
            if (!claimed)
            {
                // The recovery loop saw the stored item before we could mark it and has already queued it.
                if (_isDebugEnabled) _logger.LogDebug("Notification {NotificationType} was already queued by the recovery loop", notificationType.Name);
                return;
            }
        }

        try
        {
            var queued = new QueuedNotification(workItem, persistenceId, AttemptCount: 0, parent);
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
            MediatorTelemetry.SetError(activity, ex);
            _logger.LogError(ex, "Failed to write notification {NotificationType} to channel", notificationType.Name);
            throw;
        }
    }

    /// <summary>
    /// Graceful shutdown, called by the host before it disposes the service provider: stops the maintenance loops and
    /// lets the workers finish what is queued or running, for up to <see cref="MediatorOptions.ShutdownTimeout"/> (or until
    /// the host's own shutdown timeout cancels <paramref name="cancellationToken"/>), then cancels the handlers' token.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Stop(drainQueue: true, cancellationToken);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Stops the publisher if the host has not (no generic host). The service provider is being disposed, so no DI scope
    /// can be created for a queued notification: running handlers get <see cref="MediatorOptions.ShutdownTimeout"/> to
    /// finish, and notifications still queued are logged as not handled.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // A handler that ignores cancellation may still be running, and uses these when it finishes; disposing them then
        // threw ObjectDisposedException. They hold no unmanaged resources, so leaving them to the GC is safe.
        if (await Stop(drainQueue: false, CancellationToken.None).ConfigureAwait(false))
        {
            _shutdown.Dispose();
            _stopMaintenance.Dispose();
            _stopReading.Dispose();
            _recoveryGate.Dispose();
        }
    }

    /// <summary>Stops once; later calls wait for the first. Returns whether every loop and worker has finished.</summary>
    private Task<bool> Stop(bool drainQueue, CancellationToken cancellationToken)
    {
        lock (_stopLock)
        {
            return _stopping ??= StopCoreAsync(drainQueue, cancellationToken);
        }
    }

    private async Task<bool> StopCoreAsync(bool drainQueue, CancellationToken cancellationToken)
    {
        MediatorTelemetry.UnregisterQueue(_queueSize);

        _stopMaintenance.Cancel();
        var maintenanceStopped = await WaitAsync(_maintenanceLoops, s_cancelGracePeriod, CancellationToken.None).ConfigureAwait(false);

        _channel.Writer.TryComplete();
        if (!drainQueue) _stopReading.Cancel();
        var workersStopped = await WaitAsync(_workers, _options.ShutdownTimeout, cancellationToken).ConfigureAwait(false);

        var notHandled = _channel.Reader.Count;
        if (!workersStopped || notHandled > 0)
        {
            _logger.LogWarning("Stopping with {QueuedCount} background notifications not handled{Running}. {Outcome}",
                notHandled,
                workersStopped ? "" : $" and handlers still running after the shutdown timeout ({_options.ShutdownTimeout}); cancelling them",
                _persistence != null ? "They stay in storage and are handled on the next start." : "Without persistence, they are lost.");
        }

        _stopReading.Cancel();
        _shutdown.Cancel();
        if (!workersStopped)
            workersStopped = await WaitAsync(_workers, s_cancelGracePeriod, CancellationToken.None).ConfigureAwait(false);

        return maintenanceStopped && workersStopped;
    }

    /// <summary>
    /// Persists the notification and marks it as queued by this publisher.
    /// </summary>
    /// <returns>
    /// <c>Claimed</c> is false when the recovery loop found and queued the stored item first (it can scan between the
    /// write and the mark); the caller must then not queue it again, so the notification is processed exactly once.
    /// </returns>
    private async Task<(NotificationWorkItem WorkItem, string? PersistenceId, bool Claimed)> TryPersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken)
    {
        var notificationType = workItem.NotificationType!;
        try
        {
            var serialized = _serializer!.Serialize(workItem.Notification, notificationType) ?? string.Empty;
            workItem = new NotificationWorkItem(workItem.Notification, notificationType, workItem.CreatedAt, serialized);
            if (serialized.Length == 0)
                return (workItem, null, true);

            if (_isDebugEnabled) _logger.LogDebug("Persisting notification {NotificationType}", notificationType.Name);
            var id = await _persistence!.PersistAsync(workItem, cancellationToken).ConfigureAwait(_continueOnCapturedContext);
            if (_isDebugEnabled) _logger.LogDebug("Notification {NotificationType} persisted successfully", notificationType.Name);

            if (string.IsNullOrEmpty(id))
                return (workItem, null, true);

            // Mark as in-flight before queueing so the recovery loop never re-queues it concurrently.
            return (workItem, id, _inFlightPersistedIds.TryAdd(id, 0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist notification {NotificationType}, processing in-memory only", notificationType.Name);
            return (workItem, null, true);
        }
    }

    private static void SanitizeOptions(MediatorOptions o)
    {
        if (o.ChannelCapacity <= 0) o.ChannelCapacity = 1;
        if (o.ProcessingBatchSize <= 0) o.ProcessingBatchSize = 1;
        if (o.MaxRetryAttempts < 0) o.MaxRetryAttempts = 0;
        if (o.InitialRetryDelay < TimeSpan.Zero) o.InitialRetryDelay = TimeSpan.Zero;
        if (!(o.RetryDelayMultiplier > 0)) o.RetryDelayMultiplier = 1.0; // also NaN
        if (o.CleanupInterval <= TimeSpan.Zero) o.CleanupInterval = TimeSpan.FromMinutes(1);
        if (o.ProcessingInterval <= TimeSpan.Zero) o.ProcessingInterval = TimeSpan.FromSeconds(1);
        if (o.ShutdownTimeout < TimeSpan.Zero && o.ShutdownTimeout != Timeout.InfiniteTimeSpan) o.ShutdownTimeout = TimeSpan.Zero;
    }

    private async Task RunPeriodicAsync(TimeSpan interval, Func<Task> action)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopMaintenance.Token).ConfigureAwait(false))
            {
                try
                {
                    await action().ConfigureAwait(false);
                }
                catch (Exception ex) when (!_stopMaintenance.IsCancellationRequested)
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
            // Not ReadAllAsync: it only checks the token while waiting, so a stopping worker kept taking queued items.
            // The token is checked before each item is taken, so items left behind are still counted in the queue.
            var reader = _channel.Reader;
            var stop = _stopReading.Token;
            while (await reader.WaitToReadAsync(stop).ConfigureAwait(false))
            {
                if (stop.IsCancellationRequested || !reader.TryRead(out var queued))
                    continue;

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
                    if (queued.PersistenceId != null)
                    {
                        // Recorded before the in-flight mark is removed, so a recovery scan never sees neither.
                        _settledPersistedIds[queued.PersistenceId] = Interlocked.Increment(ref _settleSequence);
                        _inFlightPersistedIds.TryRemove(queued.PersistenceId, out _);
                    }
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
        var result = DispatchResult.Success;

        if (workItem.NotificationType == null || workItem.Notification == null)
        {
            _logger.LogError("Received invalid notification work item, skipping. NotificationType={NotificationType}, Notification={Notification}",
                workItem.NotificationType?.Name ?? "null",
                workItem.Notification?.GetType().Name ?? "null");
        }
        else
        {
            result = await _invoker.DispatchAsync(workItem.NotificationType, workItem.Notification, workItem.TargetHandlerType, queued.Parent, _shutdown.Token).ConfigureAwait(false);
        }

        if (queued.PersistenceId != null)
        {
            await SettlePersistedAsync(queued, result).ConfigureAwait(false);
        }
    }

    private async Task SettlePersistedAsync(QueuedNotification queued, DispatchResult result)
    {
        var id = queued.PersistenceId!;
        var notificationType = queued.WorkItem.NotificationType;
        try
        {
            if (result.Error != null)
            {
                // The handlers could not be run at all: retry the whole item.
                await ScheduleRetryAsync(id, queued.AttemptCount, result.Error, notificationType).ConfigureAwait(false);
            }
            else if (result.Failures == null)
            {
                await _persistence!.CompleteAsync(id, _shutdown.Token).ConfigureAwait(false);
                if (_isDebugEnabled) _logger.LogDebug("Marked persisted notification {NotificationId} as complete", id);
            }
            else if (queued.WorkItem.TargetHandlerType != null || result.HandlerCount == 1)
            {
                // Only one handler is involved: retry this same item.
                await ScheduleRetryAsync(id, queued.AttemptCount, result.Failures[0].Error, notificationType).ConfigureAwait(false);
            }
            else
            {
                await SplitIntoHandlerRetriesAsync(queued, result.Failures).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (_shutdown.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shut down before the outcome could be recorded (the store may already be disposed): the item stays in
            // storage and is handled again on the next start.
            _logger.LogInformation("Shutting down: the outcome of persisted notification {NotificationId} was not recorded; it is handled again on the next start", id);
        }
    }

    /// <summary>
    /// Some of several handlers failed: persist one retry item per failed handler, then complete the original item, so
    /// the handlers that succeeded never run again.
    /// </summary>
    private async Task SplitIntoHandlerRetriesAsync(QueuedNotification queued, IReadOnlyList<HandlerFailure> failures)
    {
        var source = queued.WorkItem;
        foreach (var failure in failures)
        {
            if (!TryGetRetryTime(queued.AttemptCount, out var retryAfter))
            {
                _logger.LogWarning(failure.Error, "Max retry attempts ({MaxRetries}) reached for handler {HandlerType} of notification {NotificationId}, giving up",
                    _options.MaxRetryAttempts, failure.HandlerType.Name, queued.PersistenceId);
                MediatorTelemetry.RecordDropped(source.NotificationType);
                continue;
            }

            var retryItem = new NotificationWorkItem(source.Notification, source.NotificationType, source.CreatedAt, source.SerializedNotification)
            {
                TargetHandlerType = NotificationHandlerInvoker.HandlerKey(failure.HandlerType),
            };

            _logger.LogInformation("Scheduling retry for handler {HandlerType} of notification {NotificationId}, attempt {AttemptCount}, retry after {RetryAfter}",
                failure.HandlerType.Name, queued.PersistenceId, queued.AttemptCount + 1, retryAfter);
            MediatorTelemetry.RecordRetry(source.NotificationType);

            if (_persistence is INotificationRetryPersistence retryPersistence)
            {
                // Stored with its retry time in one step: never visible as ready before then.
                await retryPersistence.PersistForRetryAsync(retryItem, queued.AttemptCount + 1, retryAfter, failure.Error, _shutdown.Token).ConfigureAwait(false);
                continue;
            }

            // Fallback for stores without INotificationRetryPersistence: persist, then set the retry time, while the
            // recovery loop is paused so it cannot pick the item up in between.
            await _recoveryGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                var retryId = await _persistence!.PersistAsync(retryItem, _shutdown.Token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(retryId))
                    await _persistence.FailAsync(retryId, failure.Error, retryAfter, _shutdown.Token).ConfigureAwait(false);
            }
            finally
            {
                _recoveryGate.Release();
            }
        }

        await _persistence!.CompleteAsync(queued.PersistenceId!, _shutdown.Token).ConfigureAwait(false);
    }

    private async Task RecoverNotificationsAsync()
    {
        // Maintenance token: shutdown stops recovery first, so it never queues work after the queue is closed.
        await _recoveryGate.WaitAsync(_stopMaintenance.Token).ConfigureAwait(false);
        try
        {
            await RecoverPendingAsync().ConfigureAwait(false);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task RecoverPendingAsync()
    {
        if (_isDebugEnabled) _logger.LogDebug("Starting recovery of pending notifications");
        var scanStart = Interlocked.Read(ref _settleSequence);
        var pending = await _persistence!.GetPendingAsync(_options.ProcessingBatchSize, _stopMaintenance.Token).ConfigureAwait(false);
        try
        {
            await RequeuePendingAsync(pending, scanStart).ConfigureAwait(false);
        }
        finally
        {
            // Entries settled before this scan started can't be stale in any later snapshot.
            foreach (var entry in _settledPersistedIds)
            {
                if (entry.Value <= scanStart) _settledPersistedIds.TryRemove(entry);
            }
        }
    }

    private async Task RequeuePendingAsync(IEnumerable<PersistedNotificationWorkItem> pending, long scanStart)
    {

        foreach (var persistedItem in pending)
        {
            if (_stopMaintenance.IsCancellationRequested)
                return;

            if (!IsValidPersistedItem(persistedItem))
            {
                _logger.LogWarning("Invalid persisted item found during recovery, skipping. Id: {Id}", persistedItem?.Id ?? "null");
                continue;
            }

            // Finished by this process after the snapshot was taken: the snapshot is stale for this item.
            if (_settledPersistedIds.TryGetValue(persistedItem.Id, out var settledAt) && settledAt > scanStart)
                continue;

            // Already queued in this process (published or recovered earlier and not yet processed).
            if (!_inFlightPersistedIds.TryAdd(persistedItem.Id, 0))
                continue;

            // Settled between the check above and the claim: release it again.
            if (_settledPersistedIds.TryGetValue(persistedItem.Id, out settledAt) && settledAt > scanStart)
            {
                _inFlightPersistedIds.TryRemove(persistedItem.Id, out _);
                continue;
            }

            try
            {
                if (_isDebugEnabled) _logger.LogDebug("Processing recovered notification {NotificationId}", persistedItem.Id);
                RequeuePersistedItem(persistedItem);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
            {
                _inFlightPersistedIds.TryRemove(persistedItem.Id, out _);
                _logger.LogError(ex, "Failed to recover notification {NotificationId}", persistedItem.Id);
                await ScheduleRetryAsync(persistedItem.Id, persistedItem.AttemptCount, ex, persistedItem.WorkItem.NotificationType).ConfigureAwait(false);
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

        var workItem = new NotificationWorkItem(notification, source.NotificationType, source.CreatedAt, source.SerializedNotification)
        {
            TargetHandlerType = source.TargetHandlerType,
        };
        var queued = new QueuedNotification(workItem, persistedItem.Id, persistedItem.AttemptCount, default);

        if (!_channel.Writer.TryWrite(queued))
        {
            _inFlightPersistedIds.TryRemove(persistedItem.Id, out _);
            _logger.LogWarning("Channel full while re-queueing persisted notification {NotificationId}, will retry on the next recovery pass", persistedItem.Id);
            return;
        }

        if (_isDebugEnabled) _logger.LogDebug("Re-queued persisted notification {NotificationId} to channel", persistedItem.Id);
    }

    /// <summary>
    /// The time of the next attempt after <paramref name="attemptCount"/> failed attempts, or false when the maximum
    /// number of retries has been reached.
    /// </summary>
    private bool TryGetRetryTime(int attemptCount, out DateTime retryAfter)
    {
        retryAfter = default;
        if (attemptCount >= _options.MaxRetryAttempts)
            return false;

        // Computed per attempt rather than tabled up front: MaxRetryAttempts may be very large. Saturates at
        // DateTime.MaxValue, because a retry time that overflows would throw and leave the item ready to run at once.
        var now = DateTime.UtcNow;
        var maxDelayTicks = DateTime.MaxValue.Ticks - now.Ticks;
        var delayTicks = _options.InitialRetryDelay.Ticks * Math.Pow(_options.RetryDelayMultiplier, Math.Max(0, attemptCount));
        retryAfter = delayTicks < maxDelayTicks
            ? now.AddTicks(Math.Min((long)delayTicks, maxDelayTicks)) // Min: the cast can round up past the limit
            : DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        return true;
    }

    private async Task ScheduleRetryAsync(string id, int attemptCount, Exception failure, Type? notificationType)
    {
        if (!TryGetRetryTime(attemptCount, out var retryAfter))
        {
            _logger.LogWarning(failure, "Max retry attempts ({MaxRetries}) reached for notification {NotificationId}, giving up", _options.MaxRetryAttempts, id);
            MediatorTelemetry.RecordDropped(notificationType);
            await _persistence!.CompleteAsync(id, _shutdown.Token).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Scheduling retry for persisted notification {NotificationId}, attempt {AttemptCount}, retry after {RetryAfter}", id, attemptCount + 1, retryAfter);

        MediatorTelemetry.RecordRetry(notificationType);
        await _persistence!.FailAsync(id, failure, retryAfter, _shutdown.Token).ConfigureAwait(false);
    }

    private async Task CleanupAsync()
    {
        var cutoffDate = DateTime.UtcNow.Subtract(_options.CleanupRetentionPeriod);
        if (_isDebugEnabled) _logger.LogDebug("Running cleanup of persisted notifications before {CutoffDate}", cutoffDate);
        await _persistence!.CleanupAsync(cutoffDate, _stopMaintenance.Token).ConfigureAwait(false);
        _logger.LogInformation("Cleanup completed for persisted notifications before {CutoffDate}", cutoffDate);
    }

    /// <summary>
    /// Waits for the tasks up to <paramref name="timeout"/>, or until <paramref name="cancellationToken"/> is cancelled;
    /// returns whether they all finished.
    /// </summary>
    private static async Task<bool> WaitAsync(Task[] tasks, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (tasks.Length == 0) return true;
        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            // The loops handle their own exceptions; a faulted or cancelled task has finished all the same.
        }
        return true;
    }

    /// <summary>
    /// A notification queued for background processing, with its persistence id when it was persisted and the trace
    /// context of the code that published it.
    /// </summary>
    private readonly record struct QueuedNotification(NotificationWorkItem WorkItem, string? PersistenceId, int AttemptCount, ActivityContext Parent);
}
