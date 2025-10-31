using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// High-performance mediator implementation with background processing and persistence support.
/// </summary>
public class Mediator : IMediator, IDisposable
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
    private readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task<object>>> _requestInvokers = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> _commandInvokers = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> _notificationInvokers = new();
    private readonly ConcurrentDictionary<Type, Type> _handlerTypeCache = new();
    private readonly ConcurrentDictionary<Type, object[]> _behaviorCache = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, Func<Task<object>>, CancellationToken, Task<object>>> _behaviorInvokers = new();
    
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task[] _backgroundTasks;
    private readonly Timer? _recoveryTimer;
    private readonly Timer? _cleanupTimer;
    
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the Mediator class.
    /// </summary>
    public Mediator(
        IServiceProvider serviceProvider,
        ILogger<Mediator> logger,
        IOptions<MediatorOptions> options,
        INotificationPersistence persistence,
        INotificationSerializer serializer)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
        _persistence = persistence;
        _serializer = serializer;

        var channelOptions = new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        _notificationChannel = Channel.CreateBounded<NotificationWorkItem>(channelOptions);
        _channelWriter = _notificationChannel.Writer;
        _channelReader = _notificationChannel.Reader;

        _backgroundTasks = new Task[_options.NotificationWorkerCount];
        for (var i = 0; i < _options.NotificationWorkerCount; i++)
        {
            _backgroundTasks[i] = Task.Run(ProcessNotifications, _cancellationTokenSource.Token);
        }

        if (_options.EnablePersistence)
        {
            _recoveryTimer = new Timer(RecoverNotifications, null, _options.ProcessingInterval, _options.ProcessingInterval);
            _cleanupTimer = new Timer(Cleanup, null, _options.CleanupInterval, _options.CleanupInterval);
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        var requestType = request.GetType();
        
        var behaviors = GetCachedBehaviors(requestType, typeof(TResponse));
        
        if (behaviors.Length > 0)
        {
            return await SendWithBehaviors<TResponse>(request, behaviors, requestType, cancellationToken);
        }
        
        var invoker = GetOrCreateRequestInvoker(requestType, typeof(TResponse));
        var handler = GetCachedHandler(requestType, typeof(TResponse));

        var result = await invoker(handler, request, cancellationToken);
        return (TResponse)result;
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
            var currentDelegate = handlerDelegate;
            var behavior = behaviors[i];
            var shouldUseConfigureAwait = ShouldUseConfigureAwait();
            
            handlerDelegate = async () =>
            {
                if (shouldUseConfigureAwait)
                {
                    return await behaviorInvoker(behavior, request, currentDelegate, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    return await behaviorInvoker(behavior, request, currentDelegate, cancellationToken);
                }
            };
        }

        var result = await handlerDelegate();
        return (TResponse)result;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        var requestType = typeof(TRequest);
        var invoker = GetOrCreateCommandInvoker(requestType);
        var handler = GetCachedHandler(requestType, null);

        await invoker(handler, request!, cancellationToken);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        var requestType = request.GetType();
        var invoker = GetOrCreateCommandInvoker(requestType);
        var handler = GetCachedHandler(requestType, null);

        await invoker(handler, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        var workItem = new NotificationWorkItem
        {
            Notification = notification,
            NotificationType = typeof(TNotification),
            CreatedAt = DateTime.UtcNow,
            SerializedNotification = _serializer.Serialize(notification, typeof(TNotification))
        };

        if (_options.EnablePersistence && _persistence != null)
        {
            try
            {
                if (_options.UseConfigureAwaitGlobally)
                {
                    await _persistence.PersistAsync(workItem, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _persistence.PersistAsync(workItem, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist notification, processing in-memory only");
            }
        }

        if (_options.UseConfigureAwaitGlobally)
        {
            await _channelWriter.WriteAsync(workItem, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _channelWriter.WriteAsync(workItem, cancellationToken);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] GetCachedBehaviors(Type requestType, Type responseType)
    {
        return _behaviorCache.GetOrAdd(requestType, _ =>
        {
            var behaviorType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, responseType);
            return _serviceProvider.GetServices(behaviorType).Where(b => b != null).ToArray()!;
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Type GetOrCreateHandlerType(Type requestType, Type responseType)
    {
        return _handlerTypeCache.GetOrAdd(requestType, _ => 
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
            var requestType = behaviorType.GetGenericArguments()[0];
            var responseType = behaviorType.GetGenericArguments()[1];
            var handleMethod = behaviorType.GetMethod("Handle")!;
            
            var behaviorParam = Expression.Parameter(typeof(object), "behavior");
            var requestParam = Expression.Parameter(typeof(object), "request");
            var nextParam = Expression.Parameter(typeof(Func<Task<object>>), "next");
            var tokenParam = Expression.Parameter(typeof(CancellationToken), "token");
            
            var behaviorCast = Expression.Convert(behaviorParam, behaviorType);
            var requestCast = Expression.Convert(requestParam, requestType);
            
            var delegateType = typeof(RequestHandlerDelegate<>).MakeGenericType(responseType);
            
            var convertLambda = Expression.Lambda(delegateType,
                Expression.Call(typeof(Mediator), nameof(ConvertObjectTask), new[] { responseType }, 
                    Expression.Call(nextParam, typeof(Func<Task<object>>).GetMethod("Invoke")!)));
            
            var call = Expression.Call(behaviorCast, handleMethod, requestCast, convertLambda, tokenParam);
            var convertResult = Expression.Call(typeof(Mediator), nameof(ConvertTaskToObject), new[] { responseType }, call);
            
            return Expression.Lambda<Func<object, object, Func<Task<object>>, CancellationToken, Task<object>>>(
                convertResult, behaviorParam, requestParam, nextParam, tokenParam).Compile();
        });
    }

    private static async Task<TResponse> ConvertObjectTask<TResponse>(Task<object> task)
    {
        var result = await task;
        return (TResponse)result;
    }

    private static async Task<object> ConvertTaskToObject<TResponse>(Task<TResponse> task)
    {
        var result = await task;
        return result!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, CancellationToken, Task<object>> GetOrCreateRequestInvoker(Type requestType, Type responseType)
    {
        return _requestInvokers.GetOrAdd(requestType, _ =>
        {
            var handlerType = typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType);
            var handleMethod = handlerType.GetMethod("Handle")!;
            
            var handlerParam = Expression.Parameter(typeof(object), "handler");
            var requestParam = Expression.Parameter(typeof(object), "request");
            var tokenParam = Expression.Parameter(typeof(CancellationToken), "token");
            
            var handlerCast = Expression.Convert(handlerParam, handlerType);
            var requestCast = Expression.Convert(requestParam, requestType);
            
            var call = Expression.Call(handlerCast, handleMethod, requestCast, tokenParam);
            
            var shouldUseConfigureAwait = ShouldUseConfigureAwait();
            var configureAwaitConstant = Expression.Constant(shouldUseConfigureAwait);
            
            var configureAwaitCall = Expression.Call(
                typeof(Mediator), 
                nameof(ApplyConfigureAwaitToTaskWithResult),
                new[] { responseType },
                call,
                configureAwaitConstant);
            
            return Expression.Lambda<Func<object, object, CancellationToken, Task<object>>>(
                configureAwaitCall, handlerParam, requestParam, tokenParam).Compile();
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, CancellationToken, Task> GetOrCreateCommandInvoker(Type requestType)
    {
        return _commandInvokers.GetOrAdd(requestType, _ =>
        {
            var handlerType = typeof(IRequestHandler<>).MakeGenericType(requestType);
            var handleMethod = handlerType.GetMethod("Handle")!;
            
            var handlerParam = Expression.Parameter(typeof(object), "handler");
            var requestParam = Expression.Parameter(typeof(object), "request");
            var tokenParam = Expression.Parameter(typeof(CancellationToken), "token");
            
            var handlerCast = Expression.Convert(handlerParam, handlerType);
            var requestCast = Expression.Convert(requestParam, requestType);
            
            var call = Expression.Call(handlerCast, handleMethod, requestCast, tokenParam);
            
            var shouldUseConfigureAwait = ShouldUseConfigureAwait();
            var configureAwaitConstant = Expression.Constant(shouldUseConfigureAwait);
            
            var configureAwaitCall = Expression.Call(
                typeof(Mediator),
                nameof(ApplyConfigureAwaitToTask),
                Type.EmptyTypes,
                call,
                configureAwaitConstant);
            
            return Expression.Lambda<Func<object, object, CancellationToken, Task>>(
                configureAwaitCall, handlerParam, requestParam, tokenParam).Compile();
        });
    }

    private Func<object, object, CancellationToken, Task> GetOrCreateNotificationInvoker(Type notificationType)
    {
        return _notificationInvokers.GetOrAdd(notificationType, _ =>
        {
            var handlerType = typeof(INotificationHandler<>).MakeGenericType(notificationType);
            var handleMethod = handlerType.GetMethod("Handle")!;
            
            var handlerParam = Expression.Parameter(typeof(object), "handler");
            var notificationParam = Expression.Parameter(typeof(object), "notification");
            var tokenParam = Expression.Parameter(typeof(CancellationToken), "token");
            
            var handlerCast = Expression.Convert(handlerParam, handlerType);
            var notificationCast = Expression.Convert(notificationParam, notificationType);
            
            var call = Expression.Call(handlerCast, handleMethod, notificationCast, tokenParam);
            
            var shouldUseConfigureAwait = ShouldUseConfigureAwait();
            var configureAwaitConstant = Expression.Constant(shouldUseConfigureAwait);
            
            var configureAwaitCall = Expression.Call(
                typeof(Mediator),
                nameof(ApplyConfigureAwaitToTask),
                Type.EmptyTypes,
                call,
                configureAwaitConstant);
            
            return Expression.Lambda<Func<object, object, CancellationToken, Task>>(
                configureAwaitCall, handlerParam, notificationParam, tokenParam).Compile();
        });
    }

    internal static async Task<object> ApplyConfigureAwaitToTaskWithResult<TResponse>(Task<TResponse> task, bool useConfigureAwait)
    {
        TResponse result;
        if (useConfigureAwait)
        {
            result = await task.ConfigureAwait(false);
        }
        else
        {
            result = await task;
        }
        return result!;
    }

    internal static async Task ApplyConfigureAwaitToTask(Task task, bool useConfigureAwait)
    {
        if (useConfigureAwait)
        {
            await task.ConfigureAwait(false);
        }
        else
        {
            await task;
        }
    }

    /// <summary>
    /// Determines whether to use ConfigureAwait(false) based on global settings and handler attributes.
    /// </summary>
    /// <param name="handlerType">The type of the handler.</param>
    /// <returns>True if ConfigureAwait(false) should be used, false otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldUseConfigureAwaitForType(Type handlerType)
    {
        return _options.UseConfigureAwaitGlobally;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldUseConfigureAwait()
    {
        return _options.UseConfigureAwaitGlobally;
    }

    private async Task ProcessNotifications()
    {
        await foreach (var workItem in _channelReader.ReadAllAsync(_cancellationTokenSource.Token))
        {
            if (workItem.NotificationType == null || workItem.Notification == null)
            {
                _logger.LogWarning("Received invalid notification work item, skipping");
                continue;
            }

            await ProcessNotification(workItem);
        }
    }

    private async Task ProcessNotification(NotificationWorkItem workItem)
    {
        var notificationType = workItem.NotificationType!;
        var notification = workItem.Notification!;

        var handlerType = typeof(INotificationHandler<>).MakeGenericType(notificationType);
        var handlers = _serviceProvider.GetServices(handlerType).Where(h => h != null);
        
        if (!handlers.Any())
        {
            _logger.LogDebug("No handlers found for notification type {NotificationType}", notificationType.Name);
            return;
        }

        var invoker = GetOrCreateNotificationInvoker(notificationType);
        var tasks = handlers.Select(async handler =>
        {
            try
            {
                await invoker(handler!, notification, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Notification handler {HandlerType} failed", handler?.GetType().Name ?? "Unknown");
            }
        });

        await Task.WhenAll(tasks);
    }

    private void RecoverNotifications(object? state)
    {
        if (!_options.EnablePersistence || _persistence == null) return;

        try
        {
            var pendingNotifications = _persistence.GetPendingAsync(_options.ProcessingBatchSize, _cancellationTokenSource.Token).Result;

            foreach (var persistedItem in pendingNotifications)
            {
                if (!IsValidPersistedItem(persistedItem))
                {
                    _logger.LogWarning("Invalid persisted item found, skipping. Id: {Id}", persistedItem?.Id ?? "null");
                    continue;
                }

                try
                {
                    ProcessPersistedItem(persistedItem!);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to recover notification {NotificationId}", persistedItem!.Id);
                    HandleRetry(persistedItem);
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

    private void ProcessPersistedItem(Persistence.PersistedNotificationWorkItem persistedItem)
    {
        var notification = _serializer.Deserialize(persistedItem.WorkItem.SerializedNotification, persistedItem.WorkItem.NotificationType!);
        
        var workItem = new NotificationWorkItem
        {
            Notification = notification,
            NotificationType = persistedItem.WorkItem.NotificationType,
            CreatedAt = persistedItem.WorkItem.CreatedAt,
            SerializedNotification = persistedItem.WorkItem.SerializedNotification
        };

        if (_channelWriter.TryWrite(workItem))
        {
            _persistence?.CompleteAsync(persistedItem.Id, _cancellationTokenSource.Token).Wait();
        }
        else
        {
            HandleRetry(persistedItem);
        }
    }

    private void HandleRetry(Persistence.PersistedNotificationWorkItem item)
    {
        if (item.AttemptCount >= _options.MaxRetryAttempts)
        {
            _logger.LogWarning("Max retry attempts reached for notification {NotificationId}, giving up", item.Id);
            return;
        }

        var delay = TimeSpan.FromTicks((long)(_options.InitialRetryDelay.Ticks * Math.Pow(_options.RetryDelayMultiplier, item.AttemptCount)));
        var retryAfter = DateTime.UtcNow.Add(delay);
        
        _persistence?.FailAsync(item.Id, new Exception("Retry scheduled"), retryAfter, _cancellationTokenSource.Token).Wait();
    }

    private void Cleanup(object? state)
    {
        if (!_options.EnablePersistence || _persistence == null) return;

        try
        {
            var cutoffDate = DateTime.UtcNow.Subtract(_options.CleanupRetentionPeriod);
            _persistence.CleanupAsync(cutoffDate, _cancellationTokenSource.Token).Wait();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old notifications");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        _cancellationTokenSource.Cancel();
        _channelWriter.Complete();
        
        if (_backgroundTasks.Length > 0)
        {
            try
            {
                Task.WaitAll(_backgroundTasks, TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }
        
        _recoveryTimer?.Dispose();
        _cleanupTimer?.Dispose();
        _cancellationTokenSource?.Dispose();
        
        _disposed = true;
    }
}
