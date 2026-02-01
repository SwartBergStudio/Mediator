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

    private readonly ConcurrentDictionary<(Type request, Type response), Func<object, object, CancellationToken, Task<object>>> _requestInvokers = new();
    private readonly ConcurrentDictionary<(Type request, Type response), Type> _handlerTypeCache = new();
    private readonly ConcurrentDictionary<(Type request, Type response), object[]> _behaviorCache = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, Func<Task<object>>, CancellationToken, Task<object>>> _behaviorInvokers = new();

    private static readonly MethodInfo s_invokeBehaviorMethod = typeof(RequestDispatcher).GetMethod(nameof(InvokeBehavior), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_invokeRequestHandlerMethod = typeof(RequestDispatcher).GetMethod(nameof(InvokeRequestHandler), BindingFlags.NonPublic | BindingFlags.Static)!;

    public RequestDispatcher(IScopeProvider scopeProvider, ILogger<RequestDispatcher> logger, IOptions<MediatorOptions> options)
    {
        _scopeProvider = scopeProvider;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        var requestType = request.GetType();
        _logger.LogInformation("Processing request {RequestType}", requestType.Name);
        
        try
        {
            using var scope = _scopeProvider.CreateScope();
            var scopedProvider = scope.ServiceProvider;
            var behaviors = GetScopedBehaviors(requestType, typeof(TResponse), scopedProvider);
            _logger.LogDebug("Discovered {BehaviorCount} behaviors for {RequestType}", behaviors.Length, requestType.Name);

            if (behaviors.Length > 0)
            {
                _logger.LogDebug("Executing request {RequestType} with {BehaviorCount} pipeline behaviors", requestType.Name, behaviors.Length);
                return await SendWithBehaviors<TResponse>(request, behaviors, requestType, cancellationToken, scopedProvider);
            }

            var invoker = GetOrCreateRequestInvoker(requestType, typeof(TResponse));
            var handler = GetScopedHandler(requestType, typeof(TResponse), scopedProvider);
            _logger.LogDebug("Invoking handler for request {RequestType}", requestType.Name);

            var task = invoker(handler, request, cancellationToken);
            if (task.IsCompletedSuccessfully)
            {
                var result = (TResponse)task.GetAwaiter().GetResult();
                _logger.LogInformation("Request {RequestType} completed successfully", requestType.Name);
                return result;
            }

            var awaited = (TResponse)await AwaitConfigurable(task);
            _logger.LogInformation("Request {RequestType} completed successfully", requestType.Name);
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

        return (TResponse)await AwaitConfigurable(task);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task AwaitConfigurable(Task task)
    {
        if (_options.UseConfigureAwaitGlobally)
            await task.ConfigureAwait(false);
        else
            await task;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async Task<T> AwaitConfigurable<T>(Task<T> task)
    {
        if (_options.UseConfigureAwaitGlobally)
            return await task.ConfigureAwait(false);
        else
            return await task;
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
    private Type GetOrCreateHandlerType(Type requestType, Type responseType)
    {
        return _handlerTypeCache.GetOrAdd((requestType, responseType), _ =>
            typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] GetScopedBehaviors(Type requestType, Type responseType, IServiceProvider scopedProvider)
    {
        var behaviorType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, responseType);
        var services = scopedProvider.GetServices(behaviorType);
        return MaterializeObjects(services);
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
        _logger.LogDebug("Resolved handler for request {RequestType} returning {ResponseType}", requestType.Name, responseType.Name);
        return handler;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object[] MaterializeObjects(IEnumerable<object?> source)
    {
        if (source is object[] oa)
        {
            var outList = new List<object>(oa.Length);
            for (int i = 0; i < oa.Length; i++)
            {
                if (oa[i] != null)
                    outList.Add(oa[i]!);
            }
            return outList.ToArray();
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

        var list = new List<object>();
        foreach (var s in source)
        {
            if (s != null) list.Add(s);
        }
        return list.ToArray();
    }
}
