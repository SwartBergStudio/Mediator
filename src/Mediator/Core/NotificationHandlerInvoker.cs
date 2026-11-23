using System.Buffers;
using System.Diagnostics;
using Mediator.Core.Wrappers;
using Microsoft.Extensions.Logging;

namespace Mediator.Core;

/// <summary>
/// Runs a notification's handlers. Shared by the in-process background workers (<see cref="NotificationPublisher"/>) and
/// the transport consumer side (<see cref="NotificationDispatcher"/>), so both give handlers the same scopes, isolation
/// and telemetry.
/// </summary>
internal sealed class NotificationHandlerInvoker
{
    private readonly IScopeProvider _scopeProvider;
    private readonly ILogger _logger;
    private readonly bool _isDebugEnabled;

    public NotificationHandlerInvoker(IScopeProvider scopeProvider, ILogger logger)
    {
        _scopeProvider = scopeProvider;
        _logger = logger;
        _isDebugEnabled = logger.IsEnabled(LogLevel.Debug);
    }

    /// <summary>
    /// Invokes the notification's handlers (or only <paramref name="targetHandler"/>) concurrently, each in a DI scope of
    /// its own. Handler failures are isolated, logged and returned. Failing to create the scope or resolve the handlers
    /// fails the whole notification, so it is retried like a failed handler instead of being lost or repeated at once.
    /// </summary>
    /// <remarks>
    /// One scope per handler: handlers run at the same time, so a shared scope meant sharing scoped services that are
    /// not thread-safe (a DbContext), and one handler's failed, half-saved state reaching another's SaveChanges. A retry
    /// already ran one handler alone in a fresh scope, so every run now has the same isolation. A scope can only resolve
    /// all of a notification's handlers, so each extra scope resolves them all and uses its own one; a notification with a
    /// single handler (the common case) still needs one scope.
    /// </remarks>
    public async Task<DispatchResult> DispatchAsync(Type notificationType, object notification, string? targetHandler, ActivityContext parent, CancellationToken token)
    {
        if (_isDebugEnabled) _logger.LogDebug("Processing notification {NotificationType}", notificationType.Name);

        var wrapper = HandlerWrapperCache.GetNotificationWrapper(notificationType);
        if (wrapper == null)
        {
            _logger.LogWarning("No handler wrapper registered for notification type {NotificationType}; it will not be handled. Register handlers with the source generator when running with Native AOT.", notificationType.Name);
            return DispatchResult.Success;
        }

        AsyncServiceScope scope;
        try
        {
            scope = new AsyncServiceScope(_scopeProvider.CreateScope());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create a DI scope for notification {NotificationType}", notificationType.Name);
            return DispatchResult.Failed(ex);
        }

        try
        {
            object[] handlers;
            try
            {
                handlers = wrapper.ResolveHandlers(scope.ServiceProvider);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not resolve the handlers of notification {NotificationType}", notificationType.Name);
                return DispatchResult.Failed(ex);
            }

            // A retry is for one handler that failed earlier: the other handlers already succeeded.
            var selected = targetHandler == null ? null : IndicesOf(handlers, targetHandler);
            if (selected is { Length: 0 })
            {
                _logger.LogWarning("Handler {HandlerType} for notification {NotificationType} is no longer registered; dropping its retry", targetHandler, notificationType.Name);
                return DispatchResult.Success;
            }

            var count = selected?.Length ?? handlers.Length;
            if (_isDebugEnabled) _logger.LogDebug("Discovered {HandlerCount} handlers for notification type {NotificationType}", count, notificationType.Name);

            if (count == 0)
                return DispatchResult.Success;

            var first = handlers[selected?[0] ?? 0];
            if (count == 1)
            {
                var failure = await InvokeHandlerAsync(wrapper, first, notification, notificationType, parent, token).ConfigureAwait(false);
                return failure == null ? DispatchResult.Success : new DispatchResult(1, new[] { new HandlerFailure(first.GetType(), failure) });
            }

            var pool = ArrayPool<Task<Exception?>>.Shared;
            var tasks = pool.Rent(count);
            try
            {
                // Start every handler before awaiting so they run concurrently: the first in this scope, each other one
                // in a scope of its own.
                tasks[0] = InvokeHandlerAsync(wrapper, first, notification, notificationType, parent, token);
                for (var i = 1; i < count; i++)
                {
                    tasks[i] = InvokeInOwnScopeAsync(wrapper, selected?[i] ?? i, handlers.Length, notification, notificationType, parent, token);
                }

                List<HandlerFailure>? failures = null;
                for (var i = 0; i < count; i++)
                {
                    var failure = await tasks[i].ConfigureAwait(false);
                    if (failure != null) (failures ??= new List<HandlerFailure>()).Add(new HandlerFailure(handlers[selected?[i] ?? i].GetType(), failure));
                }

                if (_isDebugEnabled) _logger.LogDebug("All {HandlerCount} handlers completed for {NotificationType}", count, notificationType.Name);
                return failures == null ? new DispatchResult(count, null) : new DispatchResult(count, failures);
            }
            finally
            {
                Array.Clear(tasks, 0, count);
                pool.Return(tasks);
            }
        }
        finally
        {
            await DisposeScopeAsync(scope, notificationType).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Identifies a handler across restarts and upgrades: type and assembly name, without the version.
    /// </summary>
    public static string HandlerKey(Type handlerType) => $"{handlerType.FullName}, {handlerType.Assembly.GetName().Name}";

    /// <summary>
    /// Runs the handler at <paramref name="index"/> in a new DI scope. Returns its failure, or <c>null</c> on success.
    /// </summary>
    private async Task<Exception?> InvokeInOwnScopeAsync(NotificationHandlerWrapper wrapper, int index, int expectedCount, object notification, Type notificationType, ActivityContext parent, CancellationToken token)
    {
        AsyncServiceScope scope;
        try
        {
            scope = new AsyncServiceScope(_scopeProvider.CreateScope());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create a DI scope for notification {NotificationType}", notificationType.Name);
            return ex;
        }

        try
        {
            object handler;
            try
            {
                var handlers = wrapper.ResolveHandlers(scope.ServiceProvider);
                if (handlers.Length != expectedCount)
                    throw new InvalidOperationException($"Notification {notificationType.Name} resolved {handlers.Length} handlers in one scope and {expectedCount} in another.");
                handler = handlers[index];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not resolve the handlers of notification {NotificationType}", notificationType.Name);
                return ex;
            }

            return await InvokeHandlerAsync(wrapper, handler, notification, notificationType, parent, token).ConfigureAwait(false);
        }
        finally
        {
            await DisposeScopeAsync(scope, notificationType).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the scope asynchronously, which also disposes scoped services that only implement IAsyncDisposable (a
    /// synchronous Dispose throws for those). A failure is logged: the handlers have finished, so it must not turn their
    /// result into a failure.
    /// </summary>
    private async ValueTask DisposeScopeAsync(AsyncServiceScope scope, Type notificationType)
    {
        try
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Disposing the DI scope of notification {NotificationType} failed", notificationType.Name);
        }
    }

    private async Task<Exception?> InvokeHandlerAsync(NotificationHandlerWrapper wrapper, object handler, object notification, Type notificationType, ActivityContext parent, CancellationToken token)
    {
        var handlerType = handler.GetType();
        using var activity = MediatorTelemetry.StartActivity("handle", notificationType, parent, ActivityKind.Consumer);
        activity?.SetTag(MediatorTelemetry.HandlerTypeTag, handlerType.FullName);
        var start = Stopwatch.GetTimestamp();

        try
        {
            if (_isDebugEnabled) _logger.LogDebug("Calling Handle method on {HandlerType}", handlerType.Name);
            await wrapper.Handle(handler, notification, token).ConfigureAwait(false);
            if (_isDebugEnabled) _logger.LogDebug("Handler {HandlerType} completed successfully", handlerType.Name);
            MediatorTelemetry.RecordHandler(notificationType, handlerType, start, null);
            return null;
        }
        catch (Exception ex)
        {
            MediatorTelemetry.SetError(activity, ex);
            MediatorTelemetry.RecordHandler(notificationType, handlerType, start, ex);
            _logger.LogError(ex, "Notification handler {HandlerType} failed with exception", handlerType.Name);
            return ex;
        }
    }

    /// <summary>The positions of the handlers whose <see cref="HandlerKey"/> is <paramref name="targetHandler"/>.</summary>
    private static int[] IndicesOf(object[] handlers, string targetHandler)
    {
        var matches = new List<int>(1);
        for (var i = 0; i < handlers.Length; i++)
        {
            if (HandlerKey(handlers[i].GetType()) == targetHandler) matches.Add(i);
        }
        return matches.ToArray();
    }
}

internal readonly record struct HandlerFailure(Type HandlerType, Exception Error);

/// <summary>
/// Outcome of dispatching to handlers: how many ran, which failed (<c>null</c> when none did), and the error when the
/// handlers could not be run at all.
/// </summary>
internal readonly record struct DispatchResult(int HandlerCount, IReadOnlyList<HandlerFailure>? Failures, Exception? Error = null)
{
    public static DispatchResult Success => default;

    public static DispatchResult Failed(Exception error) => new(0, null, error);
}
