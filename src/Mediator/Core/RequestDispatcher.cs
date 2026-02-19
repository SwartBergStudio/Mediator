using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// Handles Send&lt;TResponse&gt; operations with optional pipeline behaviors.
/// Responsible for request dispatching and response handling.
/// </summary>
internal sealed class RequestDispatcher : IRequestDispatcher
{
    private readonly IScopeProvider _scopeProvider;
    private readonly ILogger<RequestDispatcher> _logger;
    private readonly MediatorOptions _options;
    private readonly bool _isDebugEnabled;

    private readonly ConcurrentDictionary<(Type request, Type response), Func<object, object, CancellationToken, Task<object>>> _requestInvokers = new();
    private readonly ConcurrentDictionary<(Type request, Type response), Type> _handlerTypeCache = new();
    private readonly ConcurrentDictionary<(Type request, Type response), Type> _behaviorTypeCache = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, Func<Task<object>>, CancellationToken, Task<object>>> _behaviorInvokers = new();
    private readonly ConcurrentDictionary<(Type request, Type response), bool> _hasBehaviors = new();

    private static readonly MethodInfo s_invokeBehaviorMethod = typeof(RequestDispatcher).GetMethod(nameof(InvokeBehavior), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_invokeRequestHandlerMethod = typeof(RequestDispatcher).GetMethod(nameof(InvokeRequestHandler), BindingFlags.NonPublic | BindingFlags.Static)!;

    public RequestDispatcher(IScopeProvider scopeProvider, ILogger<RequestDispatcher> logger, IOptions<MediatorOptions> options)
    {
        _scopeProvider = scopeProvider;
        _logger = logger;
        _options = options.Value;
        _isDebugEnabled = _logger.IsEnabled(LogLevel.Debug);
    }

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        var requestType = request.GetType();
        if (_isDebugEnabled) _logger.LogDebug("Processing request {RequestType}", requestType.Name);

        try
        {
            using var scope = _scopeProvider.CreateScope();
            var scopedProvider = scope.ServiceProvider;
            var key = (requestType, typeof(TResponse));

            var hasBehaviors = _hasBehaviors.GetOrAdd(key, k =>
            {
                var behaviorType = GetOrCreateBehaviorType(k.request, k.response);
                var services = scopedProvider.GetServices(behaviorType);
                foreach (var s in services)
                {
                    if (s != null) return true;
                }
                return false;
            });

            if (hasBehaviors)
            {
                var behaviors = GetScopedBehaviors(requestType, typeof(TResponse), scopedProvider);
                if (_isDebugEnabled) _logger.LogDebug("Executing request {RequestType} with {BehaviorCount} pipeline behaviors", requestType.Name, behaviors.Length);
                return await SendWithBehaviors<TResponse>(request, behaviors, requestType, cancellationToken, scopedProvider);
            }

            var invoker = GetOrCreateRequestInvoker(requestType, typeof(TResponse));
            var handler = GetScopedHandler(requestType, typeof(TResponse), scopedProvider);

            var task = invoker(handler, request, cancellationToken);
            if (task.IsCompletedSuccessfully)
            {
                var result = (TResponse)task.GetAwaiter().GetResult();
                if (_isDebugEnabled) _logger.LogDebug("Request {RequestType} completed successfully", requestType.Name);
                return result;
            }

            var awaited = (TResponse)await InternalHelpers.AwaitConfigurable(task, _options.UseConfigureAwaitGlobally);
            if (_isDebugEnabled) _logger.LogDebug("Request {RequestType} completed successfully", requestType.Name);
            return awaited;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request {RequestType} failed with exception", request.GetType().Name);
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task<TResponse> SendWithBehaviors<TResponse>(IRequest<TResponse> request, object[] behaviors, Type requestType, CancellationToken cancellationToken, IServiceProvider scopedProvider)
    {
        var requestInvoker = GetOrCreateRequestInvoker(requestType, typeof(TResponse));
        var handler = GetScopedHandler(requestType, typeof(TResponse), scopedProvider);

        Func<Task<object>> handlerDelegate = () => requestInvoker(handler, request, cancellationToken);

        var behaviorType = GetOrCreateBehaviorType(requestType, typeof(TResponse));
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

        return (TResponse)await InternalHelpers.AwaitConfigurable(task, _options.UseConfigureAwaitGlobally);
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

    private static Task<object> InvokeRequestHandler<TRequest, TResponse>(object handlerObj, object requestObj, CancellationToken token)
        where TRequest : IRequest<TResponse>
    {
        var handler = (IRequestHandler<TRequest, TResponse>)handlerObj;
        var task = handler.Handle((TRequest)requestObj, token);
        if (task.IsCompletedSuccessfully)
            return Task.FromResult<object>(task.GetAwaiter().GetResult()!);
        return AwaitedInvokeRequestHandler<TResponse>(task);
    }

    private static async Task<object> AwaitedInvokeRequestHandler<TResponse>(Task<TResponse> task)
    {
        var result = await task.ConfigureAwait(false);
        return result!;
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

    private static Task<object> InvokeBehavior<TRequest, TResponse>(object behaviorObj, object requestObj, Func<Task<object>> next, CancellationToken token)
        where TRequest : IRequest<TResponse>
    {
        var behavior = (IPipelineBehavior<TRequest, TResponse>)behaviorObj;

        async Task<TResponse> NextTyped()
        {
            var obj = await next().ConfigureAwait(false);
            return (TResponse)obj;
        }

        var task = behavior.Handle((TRequest)requestObj, NextTyped, token);
        if (task.IsCompletedSuccessfully)
            return Task.FromResult<object>(task.GetAwaiter().GetResult()!);
        return AwaitedInvokeBehavior<TResponse>(task);
    }

    private static async Task<object> AwaitedInvokeBehavior<TResponse>(Task<TResponse> task)
    {
        var result = await task.ConfigureAwait(false);
        return result!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Type GetOrCreateHandlerType(Type requestType, Type responseType)
    {
        return _handlerTypeCache.GetOrAdd((requestType, responseType), _ =>
            typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] GetScopedBehaviors(Type requestType, Type responseType, IServiceProvider scopedProvider)
    {
        var behaviorType = GetOrCreateBehaviorType(requestType, responseType);
        var services = scopedProvider.GetServices(behaviorType);
        return InternalHelpers.MaterializeObjects(services);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Type GetOrCreateBehaviorType(Type requestType, Type responseType)
    {
        return _behaviorTypeCache.GetOrAdd((requestType, responseType), _ =>
            typeof(IPipelineBehavior<,>).MakeGenericType(requestType, responseType));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object GetScopedHandler(Type requestType, Type responseType, IServiceProvider scopedProvider)
    {
        var handlerType = GetOrCreateHandlerType(requestType, responseType);
        var handler = scopedProvider.GetService(handlerType);
        if (handler == null)
        {
            _logger.LogError("Handler not found for request type {RequestType} returning {ResponseType}", requestType.Name, responseType.Name);
            throw new InvalidOperationException($"Handler not found: {handlerType.Name}");
        }
        if (_isDebugEnabled) _logger.LogDebug("Resolved handler for request {RequestType} returning {ResponseType}", requestType.Name, responseType.Name);
        return handler;
    }
}
