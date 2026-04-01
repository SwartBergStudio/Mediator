using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// Handles CreateStream operations for streaming requests.
/// Responsible for dispatching streaming requests to their handlers.
/// </summary>
internal sealed class StreamRequestDispatcher : IStreamRequestDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StreamRequestDispatcher> _logger;
    private readonly bool _isDebugEnabled;

    private readonly ConcurrentDictionary<(Type request, Type response), Type> _handlerTypeCache = new();

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

        var handler = GetScopedHandler(requestType, typeof(TResponse));
        return InvokeHandler<TResponse>(handler, request, requestType, cancellationToken);
    }

    private async IAsyncEnumerable<TResponse> InvokeHandler<TResponse>(object handler, IStreamRequest<TResponse> request, Type requestType, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var handlerType = handler.GetType();
        var handleMethod = handlerType.GetMethod(nameof(IStreamRequestHandler<IStreamRequest<TResponse>, TResponse>.Handle),
            [requestType, typeof(CancellationToken)]);

        if (handleMethod == null)
        {
            _logger.LogError("Handle method not found on handler {HandlerType} for stream request {RequestType}", handlerType.Name, requestType.Name);
            throw new InvalidOperationException($"Handle method not found on handler: {handlerType.Name}");
        }

        var result = handleMethod.Invoke(handler, [request, cancellationToken]);
        if (result is not IAsyncEnumerable<TResponse> stream)
        {
            _logger.LogError("Handler {HandlerType} did not return IAsyncEnumerable<{ResponseType}> for stream request {RequestType}", handlerType.Name, typeof(TResponse).Name, requestType.Name);
            throw new InvalidOperationException($"Handler did not return IAsyncEnumerable<{typeof(TResponse).Name}>: {handlerType.Name}");
        }

        if (_isDebugEnabled) _logger.LogDebug("Streaming response items for request {RequestType}", requestType.Name);

        await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }

        if (_isDebugEnabled) _logger.LogDebug("Stream request {RequestType} completed", requestType.Name);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object GetScopedHandler(Type requestType, Type responseType)
    {
        var handlerType = _handlerTypeCache.GetOrAdd((requestType, responseType),
            k => typeof(IStreamRequestHandler<,>).MakeGenericType(k.request, k.response));
        var handler = _serviceProvider.GetService(handlerType);
        if (handler == null)
        {
            _logger.LogError("Handler not found for stream request type {RequestType} yielding {ResponseType}", requestType.Name, responseType.Name);
            throw new InvalidOperationException($"Handler not found: {handlerType.Name}");
        }
        if (_isDebugEnabled) _logger.LogDebug("Resolved handler for stream request {RequestType} yielding {ResponseType}", requestType.Name, responseType.Name);
        return handler;
    }
}
