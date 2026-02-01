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
    
    // legacy cache removed - replaced by _handlerFactoryCache
    private readonly ConcurrentDictionary<(Type request, Type response), Func<object, object, CancellationToken, Task<object>>> _requestInvokers = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> _commandInvokers = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> _notificationInvokers = new();
    private readonly ConcurrentDictionary<(Type request, Type response), Type> _handlerTypeCache = new();
    private readonly ConcurrentDictionary<(Type request, Type response), object[]> _behaviorCache = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, Func<Task<object>>, CancellationToken, Task<object>>> _behaviorInvokers = new();
    private readonly ConcurrentDictionary<Type, object[]> _notificationHandlerCache = new();
    // Cache factories to resolve concrete handler instances from a scoped IServiceProvider
    private readonly ConcurrentDictionary<Type, Func<IServiceProvider, object>> _handlerFactoryCache = new();
    
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task[] _backgroundTasks;

    // Cache MethodInfo for generic invocations to avoid repeated GetMethod calls
    private static readonly MethodInfo s_invokeBehaviorMethod = typeof(Mediator).GetMethod(nameof(InvokeBehavior), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_invokeRequestHandlerMethod = typeof(Mediator).GetMethod(nameof(InvokeRequestHandler), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_invokeCommandHandlerMethod = typeof(Mediator).GetMethod(nameof(InvokeCommandHandler), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_invokeNotificationHandlerMethod = typeof(Mediator).GetMethod(nameof(InvokeNotificationHandler), BindingFlags.NonPublic | BindingFlags.Static)!;

    private Task? _recoveryLoop;
    private Task? _cleanupLoop;

    private TimeSpan[] _retryDelays = Array.Empty<TimeSpan>();
    
    private bool _disposed;

    // Helper to efficiently materialize IEnumerable<object?> to object[] with minimal allocations
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object[] MaterializeObjects(IEnumerable<object?> source)
    {
        if (source is object[] oa)
        {
            int cnt = 0;
            for (int i = 0; i < oa.Length; i++) if (oa[i] != null) cnt++;
            if (cnt == oa.Length) return oa;
            var outArr = new object[cnt];
            int oi = 0;
            for (int i = 0; i < oa.Length; i++) if (oa[i] != null) outArr[oi++] = oa[i]!;
            return outArr;
        }

        if (source is System.Collections.ICollection coll)
        {
            var arr = new object[coll.Count];
            int i = 0;
            foreach (var s in source)
            {
                if (s != null) arr[i++] = s!;
            }
            if (i == arr.Length) return arr;
            Array.Resize(ref arr, i);
            return arr;
        }

        // Fallback
        var list = new List<object>();
        foreach (var s in source)
        {
            if (s != null) list.Add(s);
        }
        return list.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object[] MaterializeTypes(IEnumerable<object?> source)
    {
        if (source is object[] oa)
        {
            var outList = new List<object>(oa.Length);
            for (int i = 0; i < oa.Length; i++) if (oa[i] != null) outList.Add(oa[i]!.GetType());
            return outList.ToArray();
        }

        if (source is System.Collections.ICollection coll)
        {
            var arr = new object[coll.Count];
            int i = 0;
            foreach (var s in source)
            {
                if (s != null) arr[i++] = s.GetType();
            }
            if (i == arr.Length) return arr;
            Array.Resize(ref arr, i);
            return arr;
        }

        var list = new List<object>();
        foreach (var s in source)
        {
            if (s != null) list.Add(s.GetType());
        }
        return list.ToArray();
    }

    /// <summary>
    /// Initializes a new mediator using Microsoft.Extensions options binding.
    /// </summary>
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
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response produced by the handler.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;
        var requestType = request.GetType();
        var behaviors = GetScopedBehaviors(requestType, typeof(TResponse), scopedProvider);
        if (behaviors.Length > 0)
            return await SendWithBehaviors<TResponse>(request, behaviors, requestType, cancellationToken, scopedProvider);
        
        var invoker = GetOrCreateRequestInvoker(requestType, typeof(TResponse));
        var handler = GetScopedHandler(requestType, typeof(TResponse), scopedProvider);

        var task = invoker(handler, request, cancellationToken);
        if (task.IsCompletedSuccessfully)
            return (TResponse)task.GetAwaiter().GetResult();

        if (_options.UseConfigureAwaitGlobally)
            return (TResponse)await task.ConfigureAwait(false);
        else
            return (TResponse)await task;
    }

    /// <summary>
    /// Sends a request with pipeline behaviors if present.
    /// </summary>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="behaviors">Pipeline behaviors to apply.</param>
    /// <param name="requestType">The request type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="scopedProvider">The scoped service provider.</param>
    /// <returns>The response produced by the handler.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task<TResponse> SendWithBehaviors<TResponse>(IRequest<TResponse> request, object[] behaviors, Type requestType, CancellationToken cancellationToken, IServiceProvider scopedProvider)
    {
        var requestInvoker = GetOrCreateRequestInvoker(requestType, typeof(TResponse));
        var handler = GetScopedHandler(requestType, typeof(TResponse), scopedProvider);

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
    /// Sends a command without response to its handler.
    /// </summary>
    /// <typeparam name="TRequest">Request type.</typeparam>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        using var scope = _serviceProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;
        var requestType = typeof(TRequest);
        var invoker = GetOrCreateCommandInvoker(requestType);
        var handler = GetScopedHandler(requestType, null, scopedProvider);

        var task = invoker(handler, request!, cancellationToken);
        if (task.IsCompletedSuccessfully) return;
        if (_options.UseConfigureAwaitGlobally) await task.ConfigureAwait(false); else await task;
    }

    /// <summary>
    /// Sends a non-generic command without response to its handler.
    /// </summary>
    /// <param name="request">The request instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;
        var requestType = request.GetType();
        var invoker = GetOrCreateCommandInvoker(requestType);
        var handler = GetScopedHandler(requestType, null, scopedProvider);

        var task = invoker(handler, request, cancellationToken);
        if (task.IsCompletedSuccessfully) return;
        if (_options.UseConfigureAwaitGlobally) await task.ConfigureAwait(false); else await task;
    }

    /// <summary>
    /// Publishes a notification to all registered handlers for background processing.
    /// Each handler runs in its own scope in parallel.
    /// </summary>
    /// <typeparam name="TNotification">Notification type.</typeparam>
    /// <param name="notification">The notification instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        var notificationType = notification?.GetType() ?? typeof(TNotification);

        // Delay serialization until we know persistence will be attempted to avoid
        // paying serialization cost when persistence is disabled or when persistence fails.
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
                        if (_options.UseConfigureAwaitGlobally) await persistTask.ConfigureAwait(false); else await persistTask;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist notification, processing in-memory only");
                // Ensure we still create a work item for in-memory processing
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
                if (_options.UseConfigureAwaitGlobally) await writeTask.ConfigureAwait(false); else await writeTask;
            }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] GetCachedNotificationHandlers(Type notificationType, IServiceProvider? scopedProvider = null)
    {
        if (scopedProvider != null)
        {
            return ResolveNotificationHandlers(notificationType, scopedProvider);
        }

        // Cache concrete handler types instead of resolved instances to avoid holding
        // root-scoped instances and to enable fast resolution via factories in new scopes.
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

        // Populate factory cache for concrete handler types discovered during resolution.
        // This allows resolving the same concrete type quickly from a new scope later.
        for (int i = 0; i < arr.Length; i++)
        {
            var concreteType = arr[i].GetType();
            _handlerFactoryCache.GetOrAdd(concreteType, t => (IServiceProvider sp) => sp.GetService(t)!);
        }

        return arr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Type GetOrCreateHandlerType(Type requestType, Type responseType)
    {
        return _handlerTypeCache.GetOrAdd((requestType, responseType), _ => 
            typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, Func<Task<object>>, CancellationToken, Task<object>> GetOrCreateBehaviorInvoker(Type behaviorType)
    {
        return _behaviorInvokers.GetOrAdd(behaviorType, _ =>
        {
            var genericArgs = behaviorType.GetGenericArguments();
            var requestType = genericArgs[0];
            var responseType = genericArgs[1];
            var generic = s_invokeBehaviorMethod.MakeGenericMethod(requestType, responseType);
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
            var generic = s_invokeRequestHandlerMethod.MakeGenericMethod(requestType, responseType);
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
            var generic = s_invokeCommandHandlerMethod.MakeGenericMethod(requestType);
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
    private async Task InvokeHandlerInOwnScope(Func<object, object, CancellationToken, Task> invoker, Type handlerInterfaceType, object discoveredHandlerInstance, object notification, CancellationToken token)
    {
        // discoveredHandlerInstance may be either a concrete instance (from scoped discovery)
        // or a Type (when using cached types). Handle both cases.
        Type concreteType;
        if (discoveredHandlerInstance is Type ct) concreteType = ct; else concreteType = discoveredHandlerInstance.GetType();
        try
        {
            using var scope = _serviceProvider.CreateScope();
            // Try fast-path: resolve via cached factory for the concrete type if available.
            object? target = null;
            if (_handlerFactoryCache.TryGetValue(concreteType, out var factory))
            {
                try { target = factory(scope.ServiceProvider); }
                catch { target = null; }
            }

            if (target == null)
            {
                // Fallback: enumerate scoped services and find matching concrete type.
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
                if (_options.UseConfigureAwaitGlobally) await invocationTask.ConfigureAwait(false); else await invocationTask;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] GetScopedBehaviors(Type requestType, Type responseType, IServiceProvider scopedProvider)
    {
        var behaviorType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, responseType);
        var services = scopedProvider.GetServices(behaviorType);
        var list = new List<object>();
        foreach (var svc in services)
        {
            if (svc != null) list.Add(svc);
        }
        return list.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object GetScopedHandler(Type requestType, Type? responseType, IServiceProvider scopedProvider)
    {
        var handlerType = responseType != null 
            ? GetOrCreateHandlerType(requestType, responseType)
            : typeof(IRequestHandler<>).MakeGenericType(requestType);
        return scopedProvider.GetService(handlerType) ?? throw new InvalidOperationException($"Handler not found: {handlerType.Name}");
    }
}
