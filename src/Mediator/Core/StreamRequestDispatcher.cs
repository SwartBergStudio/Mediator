using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Mediator.Core;

/// <summary>
/// Handles CreateStream operations for streaming requests with optional pipeline behaviors.
/// Responsible for dispatching streaming requests to their handlers.
/// </summary>
internal sealed class StreamRequestDispatcher : IStreamRequestDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StreamRequestDispatcher> _logger;
    private readonly bool _isDebugEnabled;

    private readonly ConcurrentDictionary<(Type request, Type response), Type> _handlerTypeCache = new();
    private readonly ConcurrentDictionary<(Type request, Type response), Type> _behaviorTypeCache = new();
    private readonly ConcurrentDictionary<(Type request, Type response), bool> _hasBehaviors = new();

    private static readonly MethodInfo s_invokeHandlerMethod = typeof(StreamRequestDispatcher).GetMethod(nameof(InvokeStreamHandler), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo s_invokeBehaviorMethod = typeof(StreamRequestDispatcher).GetMethod(nameof(InvokeStreamBehavior), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly ConcurrentDictionary<(Type request, Type response), Func<object, object, CancellationToken, IAsyncEnumerable<object>>> _handlerInvokers = new();
    private readonly ConcurrentDictionary<Type, Func<object, object, Func<IAsyncEnumerable<object>>, CancellationToken, IAsyncEnumerable<object>>> _behaviorInvokers = new();

    public StreamRequestDispatcher(IServiceProvider serviceProvider, ILogger<StreamRequestDispatcher> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _isDebugEnabled = _logger.IsEnabled(LogLevel.Debug);
    }

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        var requestType = request.GetType();
        if (_isDebugEnabled) _logger.LogDebug("Processing stream request {RequestType}", requestType.Name);

        var scopedProvider = _serviceProvider;
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
            if (_isDebugEnabled) _logger.LogDebug("Executing stream request {RequestType} with {BehaviorCount} pipeline behaviors", requestType.Name, behaviors.Length);
            return CreateStreamWithBehaviors<TResponse>(request, behaviors, requestType, cancellationToken, scopedProvider);
        }

        var handler = GetScopedHandler(requestType, typeof(TResponse), scopedProvider);
        var invoker = GetOrCreateHandlerInvoker(requestType, typeof(TResponse));
        return WrapStream<TResponse>(invoker(handler, request, cancellationToken), cancellationToken);
    }

    private IAsyncEnumerable<TResponse> CreateStreamWithBehaviors<TResponse>(IStreamRequest<TResponse> request, object[] behaviors, Type requestType, CancellationToken cancellationToken, IServiceProvider scopedProvider)
    {
        var handler = GetScopedHandler(requestType, typeof(TResponse), scopedProvider);
        var handlerInvoker = GetOrCreateHandlerInvoker(requestType, typeof(TResponse));

        Func<IAsyncEnumerable<object>> handlerDelegate = () => handlerInvoker(handler, request, cancellationToken);

        var behaviorType = GetOrCreateBehaviorType(requestType, typeof(TResponse));
        var behaviorInvoker = GetOrCreateBehaviorInvoker(behaviorType);

        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var currentDelegate = handlerDelegate;
            handlerDelegate = () => behaviorInvoker(behavior, request, currentDelegate, cancellationToken);
        }

        return WrapStream<TResponse>(handlerDelegate(), cancellationToken);
    }

    private static async IAsyncEnumerable<TResponse> WrapStream<TResponse>(IAsyncEnumerable<object> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return (TResponse)item;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, CancellationToken, IAsyncEnumerable<object>> GetOrCreateHandlerInvoker(Type requestType, Type responseType)
    {
        return _handlerInvokers.GetOrAdd((requestType, responseType), _ =>
        {
            var generic = s_invokeHandlerMethod.MakeGenericMethod(requestType, responseType);
            return (Func<object, object, CancellationToken, IAsyncEnumerable<object>>)generic.CreateDelegate(
                typeof(Func<object, object, CancellationToken, IAsyncEnumerable<object>>));
        });
    }

    private static IAsyncEnumerable<object> InvokeStreamHandler<TRequest, TResponse>(object handlerObj, object requestObj, CancellationToken token)
        where TRequest : IStreamRequest<TResponse>
    {
        var handler = (IStreamRequestHandler<TRequest, TResponse>)handlerObj;
        return BoxStream(handler.Handle((TRequest)requestObj, token));
    }

    private static async IAsyncEnumerable<object> BoxStream<TResponse>(IAsyncEnumerable<TResponse> source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item!;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, Func<IAsyncEnumerable<object>>, CancellationToken, IAsyncEnumerable<object>> GetOrCreateBehaviorInvoker(Type behaviorType)
    {
        return _behaviorInvokers.GetOrAdd(behaviorType, _ =>
        {
            var genericArgs = behaviorType.GetGenericArguments();
            var requestType = genericArgs[0];
            var responseType = genericArgs[1];
            var generic = s_invokeBehaviorMethod.MakeGenericMethod(requestType, responseType);
            return (Func<object, object, Func<IAsyncEnumerable<object>>, CancellationToken, IAsyncEnumerable<object>>)generic.CreateDelegate(
                typeof(Func<object, object, Func<IAsyncEnumerable<object>>, CancellationToken, IAsyncEnumerable<object>>));
        });
    }

    private static IAsyncEnumerable<object> InvokeStreamBehavior<TRequest, TResponse>(object behaviorObj, object requestObj, Func<IAsyncEnumerable<object>> next, CancellationToken token)
        where TRequest : IStreamRequest<TResponse>
    {
        var behavior = (IStreamPipelineBehavior<TRequest, TResponse>)behaviorObj;

        IAsyncEnumerable<TResponse> NextTyped() => UnboxStream<TResponse>(next());

        return BoxStream(behavior.Handle((TRequest)requestObj, NextTyped, token));
    }

    private static async IAsyncEnumerable<TResponse> UnboxStream<TResponse>(IAsyncEnumerable<object> source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return (TResponse)item;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Type GetOrCreateBehaviorType(Type requestType, Type responseType)
    {
        return _behaviorTypeCache.GetOrAdd((requestType, responseType), _ =>
            typeof(IStreamPipelineBehavior<,>).MakeGenericType(requestType, responseType));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object[] GetScopedBehaviors(Type requestType, Type responseType, IServiceProvider scopedProvider)
    {
        var behaviorType = GetOrCreateBehaviorType(requestType, responseType);
        var services = scopedProvider.GetServices(behaviorType);
        return InternalHelpers.MaterializeObjects(services);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object GetScopedHandler(Type requestType, Type responseType, IServiceProvider scopedProvider)
    {
        var handlerType = _handlerTypeCache.GetOrAdd((requestType, responseType),
            k => typeof(IStreamRequestHandler<,>).MakeGenericType(k.request, k.response));
        var handler = scopedProvider.GetService(handlerType);
        if (handler == null)
        {
            _logger.LogError("Handler not found for stream request type {RequestType} yielding {ResponseType}", requestType.Name, responseType.Name);
            throw new InvalidOperationException($"Handler not found: {handlerType.Name}");
        }
        if (_isDebugEnabled) _logger.LogDebug("Resolved handler for stream request {RequestType} yielding {ResponseType}", requestType.Name, responseType.Name);
        return handler;
    }
}
