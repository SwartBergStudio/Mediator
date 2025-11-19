using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Buffers;

namespace Mediator.Core;

/// <summary>
/// High-performance mediator with background processing, optional persistence, and pipeline behaviors.
/// AOT-friendly invokers and minimized allocations for fast throughput.
/// </summary>
public sealed class Mediator : IMediator, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<Mediator> _logger;
    private readonly MediatorOptions _options;
    private readonly INotificationPersistence _persistence;
    private readonly INotificationSerializer _serializer;
    
    private readonly Channel<NotificationWorkItem> _notificationChannel;
    private readonly ChannelWriter<NotificationWorkItem> _channelWriter;
    private readonly ChannelReader<NotificationWorkItem> _channelReader;
    
    private readonly ConcurrentDictionary<Type, object> _handlerCache = new();
    private readonly ConcurrentDictionary<(Type request, Type response), Func<object, object, CancellationToken, Task<object>>> _requestInvokers = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> _commandInvokers = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> _notificationInvokers = new();
    private readonly ConcurrentDictionary<(Type request, Type response), Type> _handlerTypeCache = new();
    private readonly ConcurrentDictionary<(Type request, Type response), object[]> _behaviorCache = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, Func<Task<object>>, CancellationToken, Task<object>>> _behaviorInvokers = new();
    private readonly ConcurrentDictionary<Type, object[]> _notificationHandlerCache = new();
    
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task[] _backgroundTasks;

    private Task? _recoveryLoop;
    private Task? _cleanupLoop;

    private TimeSpan[] _retryDelays = Array.Empty<TimeSpan>();
    
    private bool _disposed;

    /// <summary>
    /// Initializes a new mediator using Microsoft.Extensions options binding.
    /// </summary>
    /// <param name="serviceProvider">Service provider used to resolve handlers and behaviors.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="options">Configured mediator options.</param>
    /// <param name="persistence">Notification persistence implementation.</param>
    /// <param name="serializer">Notification serializer implementation.</param>
    public Mediator(
        IServiceProvider serviceProvider,
        ILogger<Mediator> logger,
        IOptions<MediatorOptions> options,
        INotificationPersistence persistence,
        INotificationSerializer serializer)
        : this(serviceProvider, logger, options.Value, persistence, serializer)
    {
    }

    /// <summary>
    /// Initializes a new mediator using a direct options instance.
    /// </summary>
    /// <param name="serviceProvider">Service provider used to resolve handlers and behaviors.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="options">Mediator options.</param>
    /// <param name="persistence">Notification persistence implementation.</param>
    /// <param name="serializer">Notification serializer implementation.</param>
    public Mediator(
        IServiceProvider serviceProvider,
        ILogger<Mediator> logger,
        MediatorOptions options,
        INotificationPersistence persistence,
        INotificationSerializer serializer)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options;
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

    /// <summary>
    /// Sends a request and awaits a response from the appropriate handler.
    /// </summary>
    /// <typeparam name="TResponse">Response type.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from the handler.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        var requestType = request.GetType();
        var behaviors = GetCachedBehaviors(requestType, typeof(TResponse));
        if (behaviors.Length > 0)
            return await SendWithBehaviors<TResponse>(request, behaviors, requestType, cancellationToken);
        
        var invoker = GetOrCreateRequestInvoker(requestType, typeof(TResponse));
        var handler = GetCachedHandler(requestType, typeof(TResponse));

        var task = invoker(handler, request, cancellationToken);
        if (task.IsCompletedSuccessfully)
            return (TResponse)task.GetAwaiter().GetResult();

        if (_options.UseConfigureAwaitGlobally)
            return (TResponse)await task.ConfigureAwait(false);
        else
            return (TResponse)await task;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task<TResponse> SendWithBehaviors<TResponse>(IRequest<TResponse> request, object[] behaviors, Type requestType, CancellationToken cancellationToken)
    {
        var requestInvoker = GetOrCreateRequestInvoker(requestType, typeof(TResponse));
        var handler = GetCachedHandler(requestType, typeof(TResponse));

        Func<Task<object>> handlerDelegate = () => requestInvoker(handler, request, cancellationToken);

        var behaviorType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, typeof(TResponse));
        var behaviorInvoker = GetOrCreateBehaviorInvoker(behaviorType);

        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var currentDelegate = handlerDelegate;
            handlerDelegate = () => behaviorInvoker(behavior, request, currentDelegate, cancellationToken);
        }

        var task = handlerDelegate();
        if (task.IsCompletedSuccessfully)
            return (TResponse)task.GetAwaiter().GetResult();

        if (_options.UseConfigureAwaitGlobally)
            return (TResponse)await task.ConfigureAwait(false);
        else
            return (TResponse)await task;
    }

    /// <summary>
    /// Sends a command (no response) to its handler.
    /// </summary>
    /// <typeparam name="TRequest">Request type.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        var requestType = typeof(TRequest);
        var invoker = GetOrCreateCommandInvoker(requestType);
        var handler = GetCachedHandler(requestType, null);

        var task = invoker(handler, request!, cancellationToken);
        if (task.IsCompletedSuccessfully) return;
        if (_options.UseConfigureAwaitGlobally) await task.ConfigureAwait(false); else await task;
    }

    /// <summary>
    /// Sends a command (no response) to its handler.
    /// </summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        var requestType = request.GetType();
        var invoker = GetOrCreateCommandInvoker(requestType);
        var handler = GetCachedHandler(requestType, null);

        var task = invoker(handler, request, cancellationToken);
        if (task.IsCompletedSuccessfully) return;
        if (_options.UseConfigureAwaitGlobally) await task.ConfigureAwait(false); else await task;
    }

    /// <summary>
    /// Publishes a notification to all matching handlers. Processing is done in background workers.
    /// </summary>
    /// <typeparam name="TNotification">Notification type.</typeparam>
    /// <param name="notification">The notification instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        var notificationType = typeof(TNotification);
        string? serializedNotification = null;
        if (_options.EnablePersistence && _persistence != null)
        {
            serializedNotification = _serializer.Serialize(notification, notificationType);
        }

        var workItem = new NotificationWorkItem(
            notification,
            notificationType,
            DateTime.UtcNow,
            serializedNotification ?? string.Empty);

        if (_options.EnablePersistence && _persistence != null && !string.IsNullOrEmpty(serializedNotification))
        {
            try
            {
                var persistTask = _persistence.PersistAsync(workItem, cancellationToken);
                if (!persistTask.IsCompletedSuccessfully)
                {
                    if (_options.UseConfigureAwaitGlobally) await persistTask.ConfigureAwait(false); else await persistTask;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist notification, processing in-memory only");
            }
        }

        if (!_channelWriter.TryWrite(workItem))
        {
            var writeTask = _channelWriter.WriteAsync(workItem, cancellationToken).AsTask();
            if (!writeTask.IsCompletedSuccessfully)
            {
                if (_options.UseConfigureAwaitGlobally) await writeTask.ConfigureAwait(false); else await writeTask;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] GetCachedBehaviors(Type requestType, Type responseType)
    {
        return _behaviorCache.GetOrAdd((requestType, responseType), _ =>
        {
            var behaviorType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, responseType);
            var services = _serviceProvider.GetServices(behaviorType);
            var list = new List<object>();
            foreach (var svc in services)
            {
                if (svc != null) list.Add(svc);
            }
            return list.ToArray();
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] GetCachedNotificationHandlers(Type notificationType)
    {
        return _notificationHandlerCache.GetOrAdd(notificationType, _ =>
        {
            var handlerType = typeof(INotificationHandler<>).MakeGenericType(notificationType);
            var services = _serviceProvider.GetServices(handlerType);
            var list = new List<object>();
            foreach (var svc in services)
            {
                if (svc != null) list.Add(svc);
            }
            return list.ToArray();
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Type GetOrCreateHandlerType(Type requestType, Type responseType)
    {
        return _handlerTypeCache.GetOrAdd((requestType, responseType), _ => 
            typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object GetCachedHandler(Type requestType, Type? responseType)
    {
        var handlerType = responseType != null 
            ? GetOrCreateHandlerType(requestType, responseType)
            : typeof(IRequestHandler<>).MakeGenericType(requestType);
            
        return _handlerCache.GetOrAdd(handlerType, type => 
            _serviceProvider.GetService(type) ?? throw new InvalidOperationException($"Handler not found: {type.Name}"));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, Func<Task<object>>, CancellationToken, Task<object>> GetOrCreateBehaviorInvoker(Type behaviorType)
    {
        return _behaviorInvokers.GetOrAdd(behaviorType, _ =>
        {
            var genericArgs = behaviorType.GetGenericArguments();
            var requestType = genericArgs[0];
            var responseType = genericArgs[1];
            var method = typeof(Mediator).GetMethod(nameof(InvokeBehavior), BindingFlags.NonPublic | BindingFlags.Static)!;
            var generic = method.MakeGenericMethod(requestType, responseType);
            return (Func<object, object, Func<Task<object>>, CancellationToken, Task<object>>)generic.CreateDelegate(typeof(Func<object, object, Func<Task<object>>, CancellationToken, Task<object>>));
        });
    }

    private static async Task<object> InvokeBehavior<TRequest, TResponse>(object behaviorObj, object requestObj, Func<Task<object>> next, CancellationToken token)
        where TRequest : IRequest<TResponse>
    {
        var behavior = (IPipelineBehavior<TRequest, TResponse>)behaviorObj;

        async Task<TResponse> NextTyped()
        {
            var obj = await next();
            return (TResponse)obj;
        }

        var result = await behavior.Handle((TRequest)requestObj, NextTyped, token);
        return result!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, CancellationToken, Task<object>> GetOrCreateRequestInvoker(Type requestType, Type responseType)
    {
        return _requestInvokers.GetOrAdd((requestType, responseType), _ =>
        {
            var method = typeof(Mediator).GetMethod(nameof(InvokeRequestHandler), BindingFlags.NonPublic | BindingFlags.Static)!;
            var generic = method.MakeGenericMethod(requestType, responseType);
            return (Func<object, object, CancellationToken, Task<object>>)generic.CreateDelegate(typeof(Func<object, object, CancellationToken, Task<object>>));
        });
    }

    private static async Task<object> InvokeRequestHandler<TRequest, TResponse>(object handlerObj, object requestObj, CancellationToken token)
        where TRequest : IRequest<TResponse>
    {
        var handler = (IRequestHandler<TRequest, TResponse>)handlerObj;
        var result = await handler.Handle((TRequest)requestObj, token);
        return result!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, CancellationToken, Task> GetOrCreateCommandInvoker(Type requestType)
    {
        return _commandInvokers.GetOrAdd(requestType, _ =>
        {
            var method = typeof(Mediator).GetMethod(nameof(InvokeCommandHandler), BindingFlags.NonPublic | BindingFlags.Static)!;
            var generic = method.MakeGenericMethod(requestType);
            return (Func<object, object, CancellationToken, Task>)generic.CreateDelegate(typeof(Func<object, object, CancellationToken, Task>));
        });
    }

    private static Task InvokeCommandHandler<TRequest>(object handlerObj, object requestObj, CancellationToken token)
        where TRequest : IRequest
    {
        var handler = (IRequestHandler<TRequest>)handlerObj;
        return handler.Handle((TRequest)requestObj, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, CancellationToken, Task> GetOrCreateNotificationInvoker(Type notificationType)
    {
        return _notificationInvokers.GetOrAdd(notificationType, _ =>
        {
            var method = typeof(Mediator).GetMethod(nameof(InvokeNotificationHandler), BindingFlags.NonPublic | BindingFlags.Static)!;
            var generic = method.MakeGenericMethod(notificationType);
            return (Func<object, object, CancellationToken, Task>)generic.CreateDelegate(typeof(Func<object, object, CancellationToken, Task>));
        });
    }

    private static Task InvokeNotificationHandler<TNotification>(object handlerObj, object notificationObj, CancellationToken token)
        where TNotification : INotification
    {
        var handler = (INotificationHandler<TNotification>)handlerObj;
        return handler.Handle((TNotification)notificationObj, token);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task ProcessNotification(NotificationWorkItem workItem)
    {
        var notificationType = workItem.NotificationType!;
        var notification = workItem.Notification!;

        var handlers = GetCachedNotificationHandlers(notificationType);
        
        if (handlers.Length == 0)
        {
            _logger.LogDebug("No handlers found for notification type {NotificationType}", notificationType.Name);
            return;
        }

        var invoker = GetOrCreateNotificationInvoker(notificationType);
        
        if (handlers.Length == 1)
        {
            try
            {
                var t = invoker(handlers[0]!, notification, CancellationToken.None);
                if (t.IsCompletedSuccessfully) return;
                if (_options.UseConfigureAwaitGlobally) await t.ConfigureAwait(false); else await t;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Notification handler {HandlerType} failed", handlers[0]?.GetType().Name ?? "Unknown");
            }
        }
        else
        {
            var pool = ArrayPool<Task>.Shared;
            var tasks = pool.Rent(handlers.Length);
            var count = handlers.Length;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var handler = handlers[i];
                    tasks[i] = ProcessSingleHandler(invoker, handler!, notification);
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
                for (var i = 0; i < count; i++) tasks[i] = null!;
                pool.Return(tasks);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task ProcessSingleHandler(Func<object, object, CancellationToken, Task> invoker, object handler, object notification)
    {
        try
        {
            var t = invoker(handler, notification, CancellationToken.None);
            if (t.IsCompletedSuccessfully) return;
            if (_options.UseConfigureAwaitGlobally) await t.ConfigureAwait(false); else await t;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification handler {HandlerType} failed", handler.GetType().Name);
        }
    }
    
    private async Task RecoverNotificationsAsync()
    {
        if (!_options.EnablePersistence || _persistence == null) return;

        try
        {
            var pendingNotifications = await _persistence.GetPendingAsync(_options.ProcessingBatchSize, _cancellationTokenSource.Token).ConfigureAwait(false);
            var list = new List<Persistence.PersistedNotificationWorkItem>();
            foreach (var item in pendingNotifications)
            {
                if (item != null) list.Add(item);
            }
            if (list.Count == 0) return;

            foreach (var persistedItem in list)
            {
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
                if (_options.UseConfigureAwaitGlobally) await t.ConfigureAwait(false); else await t;
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
            if (_options.UseConfigureAwaitGlobally) await t.ConfigureAwait(false); else await t;
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
                if (_options.UseConfigureAwaitGlobally) await t.ConfigureAwait(false); else await t;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old notifications");
        }
    }

    /// <summary>
    /// Disposes the mediator, stopping background processing and releasing resources.
    /// </summary>
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
}
