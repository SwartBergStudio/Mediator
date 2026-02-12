using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Buffers;
using System.Threading.Channels;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// Publishes notifications for background processing with optional persistence.
/// Manages notification channel and background notification processing.
/// </summary>
internal sealed class NotificationPublisher : INotificationPublisher, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IScopeProvider _scopeProvider;
    private readonly ILogger<NotificationPublisher> _logger;
    private readonly MediatorOptions _options;
    private readonly INotificationPersistence _persistence;
    private readonly INotificationSerializer _serializer;

    private readonly Channel<NotificationWorkItem> _notificationChannel;
    private readonly ChannelWriter<NotificationWorkItem> _channelWriter;
    private readonly ChannelReader<NotificationWorkItem> _channelReader;

    private readonly ConcurrentDictionary<Type, object[]> _notificationHandlerCache = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> _notificationInvokers = new();

    private static readonly MethodInfo s_invokeNotificationHandlerMethod = typeof(NotificationPublisher).GetMethod(nameof(InvokeNotificationHandler), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task[] _backgroundTasks;

    private Task? _recoveryLoop;
    private Task? _cleanupLoop;
    private TimeSpan[] _retryDelays = Array.Empty<TimeSpan>();

    private bool _disposed;

    public NotificationPublisher(
        IServiceProvider serviceProvider,
        IScopeProvider scopeProvider,
        ILogger<NotificationPublisher> logger,
        IOptions<MediatorOptions> options,
        INotificationPersistence persistence,
        INotificationSerializer serializer)
    {
        _serviceProvider = serviceProvider;
        _scopeProvider = scopeProvider;
        _logger = logger;
        _options = options.Value;
        SanitizeOptions(_options);
        _persistence = persistence;
        _serializer = serializer;

        _logger.LogInformation("Initializing NotificationPublisher with EnablePersistence={EnablePersistence}, WorkerCount={WorkerCount}, ChannelCapacity={ChannelCapacity}", 
            _options.EnablePersistence, _options.NotificationWorkerCount, _options.ChannelCapacity);

        InitializeRetryDelays();

        var channelOptions = new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

        _notificationChannel = Channel.CreateBounded<NotificationWorkItem>(channelOptions);
        _channelWriter = _notificationChannel.Writer;
        _channelReader = _notificationChannel.Reader;

        _backgroundTasks = new Task[Math.Max(0, _options.NotificationWorkerCount)];
        for (var i = 0; i < _backgroundTasks.Length; i++)
        {
            _backgroundTasks[i] = Task.Run(ProcessNotifications, _cancellationTokenSource.Token);
        }
        _logger.LogInformation("Started {WorkerCount} background notification workers", _backgroundTasks.Length);

        if (_options.EnablePersistence)
        {
            _recoveryLoop = Task.Run(() => RunPeriodic(_options.ProcessingInterval, RecoverNotificationsAsync, _cancellationTokenSource.Token, _logger), _cancellationTokenSource.Token);
            _cleanupLoop = Task.Run(() => RunPeriodic(_options.CleanupInterval, CleanupAsync, _cancellationTokenSource.Token, _logger), _cancellationTokenSource.Token);
            _logger.LogInformation("Started recovery and cleanup loops with ProcessingInterval={ProcessingInterval}, CleanupInterval={CleanupInterval}", 
                _options.ProcessingInterval, _options.CleanupInterval);
        }
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        var notificationType = notification?.GetType() ?? typeof(TNotification);
        _logger.LogDebug("Publishing notification of type {NotificationType}", notificationType.Name);

        string? serializedNotification = null;
        var workItem = default(NotificationWorkItem);

        if (_options.EnablePersistence && _persistence != null)
        {
            try
            {
                serializedNotification = _serializer.Serialize(notification, notificationType);
                workItem = new NotificationWorkItem(notification, notificationType, DateTime.UtcNow, serializedNotification ?? string.Empty);

                if (!string.IsNullOrEmpty(serializedNotification))
                {
                    _logger.LogDebug("Persisting notification {NotificationType}", notificationType.Name);
                    var persistTask = _persistence.PersistAsync(workItem, cancellationToken);
                    if (!persistTask.IsCompletedSuccessfully)
                    {
                        await AwaitConfigurable(persistTask);
                    }
                    _logger.LogDebug("Notification {NotificationType} persisted successfully", notificationType.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist notification {NotificationType}, processing in-memory only", notificationType.Name);
                if (workItem.Equals(default(NotificationWorkItem)))
                {
                    workItem = new NotificationWorkItem(notification, notificationType, DateTime.UtcNow, string.Empty);
                }
            }
        }
        else
        {
            workItem = new NotificationWorkItem(notification, notificationType, DateTime.UtcNow, string.Empty);
        }

        try
        {
            if (!_channelWriter.TryWrite(workItem))
            {
                _logger.LogDebug("Channel full for {NotificationType}, waiting for space", notificationType.Name);
                var writeTask = _channelWriter.WriteAsync(workItem, cancellationToken).AsTask();
                if (!writeTask.IsCompletedSuccessfully)
                {
                    await AwaitConfigurable(writeTask);
                }
                _logger.LogDebug("Notification {NotificationType} written to channel", notificationType.Name);
            }
            else
            {
                _logger.LogDebug("Notification {NotificationType} written to channel (TryWrite succeeded)", notificationType.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write notification {NotificationType} to channel", notificationType.Name);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _cancellationTokenSource.Cancel();
        _channelWriter.Complete();

        if (_backgroundTasks.Length > 0)
        {
            try { Task.WaitAll(_backgroundTasks, TimeSpan.FromSeconds(2)); } catch { }
        }

        try
        {
            if (_recoveryLoop != null || _cleanupLoop != null)
            {
                var list = new List<Task>(2);
                if (_recoveryLoop != null) list.Add(_recoveryLoop);
                if (_cleanupLoop != null) list.Add(_cleanupLoop);
                Task.WhenAll(list).Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch { }

        _cancellationTokenSource.Dispose();

        _disposed = true;
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

    private void InitializeRetryDelays()
    {
        if (_options.MaxRetryAttempts > 0 && _options.InitialRetryDelay > TimeSpan.Zero)
        {
            var len = _options.MaxRetryAttempts;
            _retryDelays = new TimeSpan[len];
            var baseTicks = _options.InitialRetryDelay.Ticks;
            for (int i = 0; i < len; i++)
            {
                var factor = i == 0 ? 1.0 : Math.Pow(_options.RetryDelayMultiplier, i);
                _retryDelays[i] = TimeSpan.FromTicks((long)(baseTicks * factor));
            }
        }
    }

    private static async Task RunPeriodic(TimeSpan interval, Func<Task> action, CancellationToken token, ILogger logger)
    {
        if (interval <= TimeSpan.Zero)
            interval = TimeSpan.FromSeconds(1);

        var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await action().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Periodic task error occurred");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { timer.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Task AwaitConfigurable(Task task)
        => InternalHelpers.AwaitConfigurable(task, _options.UseConfigureAwaitGlobally);

    private async Task ProcessNotifications()
    {
        _logger.LogInformation("Background notification processor started");
        try
        {
            await foreach (var workItem in _channelReader.ReadAllAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
            {
                if (workItem.NotificationType == null || workItem.Notification == null)
                {
                    _logger.LogError("Received invalid notification work item, skipping. NotificationType={NotificationType}, Notification={Notification}", 
                        workItem.NotificationType?.Name ?? "null", 
                        workItem.Notification?.GetType().Name ?? "null");
                    continue;
                }

                try
                {
                    await ProcessNotification(workItem).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception while processing notification {NotificationType}", workItem.NotificationType.Name);
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

    private async Task ProcessNotification(NotificationWorkItem workItem)
    {
        var notificationType = workItem.NotificationType!;
        var notification = workItem.Notification!;
        var token = _cancellationTokenSource.Token;

        _logger.LogDebug("Processing notification {NotificationType}", notificationType.Name);

        // Create a scope for handler resolution to ensure proper DI context
        using var scope = _scopeProvider.CreateScope();
        var handlerInstances = GetCachedNotificationHandlers(notificationType, scope.ServiceProvider);
        _logger.LogDebug("Discovered {HandlerCount} handlers for notification type {NotificationType}", handlerInstances.Length, notificationType.Name);

        if (handlerInstances.Length == 0)
        {
            _logger.LogDebug("No handlers found for notification type {NotificationType}", notificationType.Name);
            return;
        }

        var invoker = GetOrCreateNotificationInvoker(notificationType);
        var handlerInterfaceType = typeof(INotificationHandler<>).MakeGenericType(notificationType);

        if (handlerInstances.Length == 1)
        {
            _logger.LogDebug("Invoking single handler for {NotificationType}", notificationType.Name);
            await InvokeHandlerInOwnScope(invoker, handlerInterfaceType, handlerInstances[0]!, notification, token).ConfigureAwait(false);
            return;
        }

        _logger.LogDebug("Invoking {HandlerCount} handlers in parallel for {NotificationType}", handlerInstances.Length, notificationType.Name);
        var pool = ArrayPool<Task>.Shared;
        var tasks = pool.Rent(handlerInstances.Length);
        var count = handlerInstances.Length;
        try
        {
            for (int i = 0; i < count; i++)
            {
                tasks[i] = InvokeHandlerInOwnScope(invoker, handlerInterfaceType, handlerInstances[i]!, notification, token);
            }
            if (_options.UseConfigureAwaitGlobally)
            {
                for (int i = 0; i < count; i++)
                {
                    var t = tasks[i];
                    if (!t.IsCompletedSuccessfully) await t.ConfigureAwait(false);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    var t = tasks[i];
                    if (!t.IsCompletedSuccessfully) await t;
                }
            }
            _logger.LogDebug("All {HandlerCount} handlers completed successfully for {NotificationType}", count, notificationType.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while invoking handlers for {NotificationType}", notificationType.Name);
            throw;
        }
        finally
        {
            for (int i = 0; i < count; i++) tasks[i] = null!;
            pool.Return(tasks);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] GetCachedNotificationHandlers(Type notificationType, IServiceProvider? scopedProvider = null)
    {
        // If scoped provider is available, always resolve from it (respects DI lifetime)
        if (scopedProvider != null)
        {
            _logger.LogDebug("Resolving handlers from scoped provider for {NotificationType}", notificationType.Name);
            return ResolveNotificationHandlers(notificationType, scopedProvider);
        }

        // Only use cache if no scoped provider (this is the fallback path, not recommended)
        return _notificationHandlerCache.GetOrAdd(notificationType, _ =>
        {
            var handlerType = typeof(INotificationHandler<>).MakeGenericType(notificationType);
            var services = _serviceProvider.GetServices(handlerType);
            var materialized = MaterializeTypes(services);
            _logger.LogDebug("Cached {HandlerCount} handler types for {NotificationType} from root service provider", materialized.Length, notificationType.Name);
            return materialized;
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] ResolveNotificationHandlers(Type notificationType, IServiceProvider provider)
    {
        var handlerType = typeof(INotificationHandler<>).MakeGenericType(notificationType);
        var services = provider.GetServices(handlerType);
        var arr = MaterializeObjects(services);
        _logger.LogDebug("Resolved {HandlerCount} handler instances for {NotificationType} from scoped provider", arr.Length, notificationType.Name);
        return arr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task InvokeHandlerInOwnScope(Func<object, object, CancellationToken, Task> invoker, Type handlerInterfaceType, object discoveredHandlerInstance, object notification, CancellationToken token)
    {
        Type concreteType;
        if (discoveredHandlerInstance is Type ct) concreteType = ct; else concreteType = discoveredHandlerInstance.GetType();
        
        _logger.LogDebug("Attempting to invoke handler {HandlerType} in scope", concreteType.Name);
        
        try
        {
            // If discoveredHandlerInstance is already an instance (from scoped resolution), use it directly
            object? target = null;
            if (discoveredHandlerInstance is not Type)
            {
                target = discoveredHandlerInstance;
                _logger.LogDebug("Handler {HandlerType} already instantiated from scoped provider", concreteType.Name);
            }
            else
            {
                // Fallback: If we only have a Type (from cache), we need to create a scope
                // This path is hit when called from legacy code paths
                using var scope = _scopeProvider.CreateScope();
                var scopedHandlers = scope.ServiceProvider.GetServices(handlerInterfaceType);
                foreach (var h in scopedHandlers)
                {
                    if (h != null && h.GetType() == concreteType)
                    {
                        target = h;
                        _logger.LogDebug("Resolved handler {HandlerType} via scope fallback", concreteType.Name);
                        break;
                    }
                }
            }

            if (target == null)
            {
                _logger.LogError("Failed to resolve handler {HandlerType}", concreteType.Name);
                return;
            }

            _logger.LogDebug("Calling Handle method on {HandlerType}", concreteType.Name);
            var invocationTask = invoker(target, notification, token);
            if (!invocationTask.IsCompletedSuccessfully)
            {
                await AwaitConfigurable(invocationTask);
            }
            _logger.LogDebug("Handler {HandlerType} completed successfully", concreteType.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification handler {HandlerType} failed with exception", concreteType.Name);
        }
    }

    private async Task RecoverNotificationsAsync()
    {
        if (!_options.EnablePersistence || _persistence == null) return;

        try
        {
            _logger.LogDebug("Starting recovery of pending notifications");
            var pendingNotifications = await _persistence.GetPendingAsync(_options.ProcessingBatchSize, _cancellationTokenSource.Token).ConfigureAwait(false);

            foreach (var persistedItem in pendingNotifications)
            {
                if (persistedItem == null) 
                {
                    _logger.LogWarning("Null persisted item encountered during recovery");
                    continue;
                }

                if (!IsValidPersistedItem(persistedItem))
                {
                    _logger.LogWarning("Invalid persisted item found during recovery, skipping. Id: {Id}", persistedItem?.Id ?? "null");
                    continue;
                }

                try
                {
                    _logger.LogDebug("Processing recovered notification {NotificationId}", persistedItem.Id);
                    await ProcessPersistedItemAsync(persistedItem!).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to recover notification {NotificationId}", persistedItem!.Id);
                    await HandleRetryAsync(persistedItem).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover notifications");
        }
    }

    private bool IsValidPersistedItem(Persistence.PersistedNotificationWorkItem? item)
    {
        return item != null &&
               !string.IsNullOrEmpty(item.Id) &&
               item.WorkItem.NotificationType != null &&
               !string.IsNullOrEmpty(item.WorkItem.SerializedNotification);
    }

    private async Task ProcessPersistedItemAsync(Persistence.PersistedNotificationWorkItem persistedItem)
    {
        try
        {
            _logger.LogDebug("Deserializing persisted notification {NotificationId}", persistedItem.Id);
            var notification = _serializer.Deserialize(persistedItem.WorkItem.SerializedNotification, persistedItem.WorkItem.NotificationType!);

            if (notification == null)
            {
                _logger.LogError("Deserialization returned null for persisted notification {NotificationId}", persistedItem.Id);
                return;
            }

            var workItem = new NotificationWorkItem(
                notification,
                persistedItem.WorkItem.NotificationType,
                persistedItem.WorkItem.CreatedAt,
                persistedItem.WorkItem.SerializedNotification);

            if (_channelWriter.TryWrite(workItem))
            {
                _logger.LogDebug("Re-queued persisted notification {NotificationId} to channel", persistedItem.Id);
                var t = _persistence!.CompleteAsync(persistedItem.Id, _cancellationTokenSource.Token);
                if (!t.IsCompletedSuccessfully)
                {
                    await AwaitConfigurable(t);
                }
                _logger.LogInformation("Marked persisted notification {NotificationId} as complete", persistedItem.Id);
            }
            else
            {
                _logger.LogWarning("Channel full while re-queueing persisted notification {NotificationId}, will retry", persistedItem.Id);
                await HandleRetryAsync(persistedItem).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing persisted notification {NotificationId}", persistedItem.Id);
            throw;
        }
    }

    private async Task HandleRetryAsync(Persistence.PersistedNotificationWorkItem item)
    {
        _logger.LogDebug("Handling retry for persisted notification {NotificationId}, attempt {AttemptCount}", item.Id, item.AttemptCount);
        
        if (item.AttemptCount >= _options.MaxRetryAttempts)
        {
            _logger.LogWarning("Max retry attempts ({MaxRetries}) reached for notification {NotificationId}, giving up", _options.MaxRetryAttempts, item.Id);
            return;
        }

        TimeSpan delay;
        if (_retryDelays.Length > 0 && item.AttemptCount >= 0 && item.AttemptCount < _retryDelays.Length)
        {
            delay = _retryDelays[item.AttemptCount];
        }
        else
        {
            delay = TimeSpan.FromTicks((long)(_options.InitialRetryDelay.Ticks * Math.Pow(_options.RetryDelayMultiplier, item.AttemptCount)));
        }

        var retryAfter = DateTime.UtcNow.Add(delay);
        _logger.LogInformation("Scheduling retry for persisted notification {NotificationId}, attempt {AttemptCount}, retry after {RetryAfter}", item.Id, item.AttemptCount + 1, retryAfter);

        var t = _persistence!.FailAsync(item.Id, new Exception("Retry scheduled"), retryAfter, _cancellationTokenSource.Token);
        if (!t.IsCompletedSuccessfully)
        {
            await AwaitConfigurable(t);
        }
    }

    private async Task CleanupAsync()
    {
        if (!_options.EnablePersistence || _persistence == null) return;
        try
        {
            var cutoffDate = DateTime.UtcNow.Subtract(_options.CleanupRetentionPeriod);
            _logger.LogDebug("Running cleanup of persisted notifications before {CutoffDate}", cutoffDate);
            var t = _persistence!.CleanupAsync(cutoffDate, _cancellationTokenSource.Token);
            if (!t.IsCompletedSuccessfully)
            {
                await AwaitConfigurable(t);
            }
            _logger.LogInformation("Cleanup completed for persisted notifications before {CutoffDate}", cutoffDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old notifications");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, CancellationToken, Task> GetOrCreateNotificationInvoker(Type notificationType)
    {
        return _notificationInvokers.GetOrAdd(notificationType, _ =>
        {
            var generic = s_invokeNotificationHandlerMethod.MakeGenericMethod(notificationType);
            return (Func<object, object, CancellationToken, Task>)generic.CreateDelegate(typeof(Func<object, object, CancellationToken, Task>));
        });
    }

    private static Task InvokeNotificationHandler<TNotification>(object handlerObj, object notificationObj, CancellationToken token)
        where TNotification : INotification
    {
        var handler = (INotificationHandler<TNotification>)handlerObj;
        return handler.Handle((TNotification)notificationObj, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object[] MaterializeObjects(IEnumerable<object?> source)
        => InternalHelpers.MaterializeObjects(source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object[] MaterializeTypes(IEnumerable<object?> source)
        => InternalHelpers.MaterializeTypes(source);
}
