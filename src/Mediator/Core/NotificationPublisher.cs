using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Buffers;
using System.Threading.Channels;
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
    private readonly ConcurrentDictionary<Type, Func<IServiceProvider, object>> _concreteHandlerFactoryCache = new();
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

        if (_options.EnablePersistence)
        {
            _recoveryLoop = Task.Run(() => RunPeriodic(_options.ProcessingInterval, RecoverNotificationsAsync, _cancellationTokenSource.Token), _cancellationTokenSource.Token);
            _cleanupLoop = Task.Run(() => RunPeriodic(_options.CleanupInterval, CleanupAsync, _cancellationTokenSource.Token), _cancellationTokenSource.Token);
        }
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        var notificationType = notification?.GetType() ?? typeof(TNotification);

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
                    var persistTask = _persistence.PersistAsync(workItem, cancellationToken);
                    if (!persistTask.IsCompletedSuccessfully)
                    {
                        await AwaitConfigurable(persistTask);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist notification, processing in-memory only");
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

        if (!_channelWriter.TryWrite(workItem))
        {
            var writeTask = _channelWriter.WriteAsync(workItem, cancellationToken).AsTask();
            if (!writeTask.IsCompletedSuccessfully)
            {
                await AwaitConfigurable(writeTask);
            }
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

    private static async Task RunPeriodic(TimeSpan interval, Func<Task> action, CancellationToken token)
    {
        if (interval <= TimeSpan.Zero)
            interval = TimeSpan.FromSeconds(1);

        var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await action().ConfigureAwait(false); }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        finally { timer.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task AwaitConfigurable(Task task)
    {
        if (_options.UseConfigureAwaitGlobally)
            await task.ConfigureAwait(false);
        else
            await task;
    }

    private async Task ProcessNotifications()
    {
        await foreach (var workItem in _channelReader.ReadAllAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
        {
            if (workItem.NotificationType == null || workItem.Notification == null)
            {
                _logger.LogWarning("Received invalid notification work item, skipping");
                continue;
            }

            await ProcessNotification(workItem).ConfigureAwait(false);
        }
    }

    private async Task ProcessNotification(NotificationWorkItem workItem)
    {
        var notificationType = workItem.NotificationType!;
        var notification = workItem.Notification!;
        var token = _cancellationTokenSource.Token;

        var handlerInstances = GetCachedNotificationHandlers(notificationType, null);

        if (handlerInstances.Length == 0)
        {
            if (_notificationHandlerCache.TryGetValue(notificationType, out var cached) && cached.Length > 0)
            {
                _logger.LogWarning("Scoped discovery returned zero handlers for {NotificationType} while cache had {CachedCount}", notificationType.Name, cached.Length);
            }
            else
            {
                _logger.LogDebug("No handlers found for notification type {NotificationType}", notificationType.Name);
            }
            return;
        }

        var invoker = GetOrCreateNotificationInvoker(notificationType);
        var handlerInterfaceType = typeof(INotificationHandler<>).MakeGenericType(notificationType);

        if (handlerInstances.Length == 1)
        {
            await InvokeHandlerInOwnScope(invoker, handlerInterfaceType, handlerInstances[0]!, notification, token).ConfigureAwait(false);
            return;
        }

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
        if (scopedProvider != null)
        {
            return ResolveNotificationHandlers(notificationType, scopedProvider);
        }

        return _notificationHandlerCache.GetOrAdd(notificationType, _ =>
        {
            var handlerType = typeof(INotificationHandler<>).MakeGenericType(notificationType);
            var services = _serviceProvider.GetServices(handlerType);
            return MaterializeTypes(services);
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] ResolveNotificationHandlers(Type notificationType, IServiceProvider provider)
    {
        var handlerType = typeof(INotificationHandler<>).MakeGenericType(notificationType);
        var services = provider.GetServices(handlerType);
        var arr = MaterializeObjects(services);

        for (int i = 0; i < arr.Length; i++)
        {
            var concreteType = arr[i].GetType();
            _concreteHandlerFactoryCache.GetOrAdd(concreteType, t => (IServiceProvider sp) => sp.GetService(t)!);
        }

        return arr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task InvokeHandlerInOwnScope(Func<object, object, CancellationToken, Task> invoker, Type handlerInterfaceType, object discoveredHandlerInstance, object notification, CancellationToken token)
    {
        Type concreteType;
        if (discoveredHandlerInstance is Type ct) concreteType = ct; else concreteType = discoveredHandlerInstance.GetType();
        try
        {
            using var scope = _scopeProvider.CreateScope();
            object? target = null;
            if (_concreteHandlerFactoryCache.TryGetValue(concreteType, out var factory))
            {
                try { target = factory(scope.ServiceProvider); }
                catch { target = null; }
            }

            if (target == null)
            {
                var scopedHandlers = scope.ServiceProvider.GetServices(handlerInterfaceType);
                foreach (var h in scopedHandlers)
                {
                    if (h != null && h.GetType() == concreteType)
                    {
                        target = h;
                        break;
                    }
                }
            }

            if (target == null)
            {
                _logger.LogWarning("Failed to resolve handler {HandlerType} in isolated scope", concreteType.Name);
                return;
            }

            var invocationTask = invoker(target, notification, token);
            if (!invocationTask.IsCompletedSuccessfully)
            {
                await AwaitConfigurable(invocationTask);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification handler {HandlerType} failed", concreteType.Name);
        }
    }

    private async Task RecoverNotificationsAsync()
    {
        if (!_options.EnablePersistence || _persistence == null) return;

        try
        {
            var pendingNotifications = await _persistence.GetPendingAsync(_options.ProcessingBatchSize, _cancellationTokenSource.Token).ConfigureAwait(false);

            foreach (var persistedItem in pendingNotifications)
            {
                if (persistedItem == null) continue;

                if (!IsValidPersistedItem(persistedItem))
                {
                    _logger.LogWarning("Invalid persisted item found, skipping. Id: {Id}", persistedItem?.Id ?? "null");
                    continue;
                }

                try
                {
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
        var notification = _serializer.Deserialize(persistedItem.WorkItem.SerializedNotification, persistedItem.WorkItem.NotificationType!);

        var workItem = new NotificationWorkItem(
            notification,
            persistedItem.WorkItem.NotificationType,
            persistedItem.WorkItem.CreatedAt,
            persistedItem.WorkItem.SerializedNotification);

        if (_channelWriter.TryWrite(workItem))
        {
            var t = _persistence!.CompleteAsync(persistedItem.Id, _cancellationTokenSource.Token);
            if (!t.IsCompletedSuccessfully)
            {
                await AwaitConfigurable(t);
            }
        }
        else
        {
            await HandleRetryAsync(persistedItem).ConfigureAwait(false);
        }
    }

    private async Task HandleRetryAsync(Persistence.PersistedNotificationWorkItem item)
    {
        if (item.AttemptCount >= _options.MaxRetryAttempts)
        {
            _logger.LogWarning("Max retry attempts reached for notification {NotificationId}, giving up", item.Id);
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
            var t = _persistence!.CleanupAsync(cutoffDate, _cancellationTokenSource.Token);
            if (!t.IsCompletedSuccessfully)
            {
                await AwaitConfigurable(t);
            }
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
    private static T[] Materialize<T>(IEnumerable<object?> source, Func<object, T> selector)
    {
        if (source is object[] oa)
        {
            var outList = new List<T>(oa.Length);
            for (int i = 0; i < oa.Length; i++)
            {
                if (oa[i] != null)
                    outList.Add(selector(oa[i]!));
            }
            return outList.ToArray();
        }

        if (source is System.Collections.ICollection coll)
        {
            var arr = new T[coll.Count];
            int i = 0;
            foreach (var s in source)
            {
                if (s != null)
                    arr[i++] = selector(s);
            }
            if (i == arr.Length) return arr;
            Array.Resize(ref arr, i);
            return arr;
        }

        var list = new List<T>();
        foreach (var s in source)
        {
            if (s != null)
                list.Add(selector(s));
        }
        return list.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object[] MaterializeObjects(IEnumerable<object?> source)
        => Materialize(source, obj => obj!);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object[] MaterializeTypes(IEnumerable<object?> source)
        => Materialize(source, obj => (object)obj!.GetType());
}
