using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mediator.Core.Wrappers;

namespace Mediator.Core;

/// <summary>
/// Request, command, stream and awaited-notification dispatch shared by <see cref="Mediator"/> and the dispatcher services.
/// </summary>
/// <remarks>
/// Each call looks up the cached, strongly-typed dispatcher for the message type and returns the pipeline's task (or
/// stream) directly. There is deliberately no async wrapper or logging here: these handlers run inside the caller's
/// await, so exceptions reach the caller (and its host's logging) unchanged and no extra state machine is allocated.
/// Only when traces or metrics are being collected (<see cref="MediatorTelemetry.IsEnabled"/>) does a call take the
/// observed path, which wraps it in an activity and records its duration.
/// Background notifications, which nobody awaits, are logged by <see cref="NotificationPublisher"/> instead.
/// </remarks>
internal static class Dispatch
{
    public static Task<TResponse> Send<TResponse>(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        if (request is null)
            return Task.FromException<TResponse>(new ArgumentNullException(nameof(request)));

        return MediatorTelemetry.IsEnabled
            ? SendObserved(request, serviceProvider, cancellationToken)
            : SendCore(request, serviceProvider, cancellationToken);
    }

    public static Task SendCommand<TRequest>(TRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        return MediatorTelemetry.IsEnabled
            ? SendCommandObserved(request, serviceProvider, cancellationToken)
            : SendCommandCore(request, serviceProvider, cancellationToken);
    }

    public static Task SendCommand(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        if (request is null)
            return Task.FromException(new ArgumentNullException(nameof(request)));

        return MediatorTelemetry.IsEnabled
            ? SendCommandObserved(request, serviceProvider, cancellationToken)
            : SendCommandCore(request, serviceProvider, cancellationToken);
    }

    public static IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolved eagerly in both paths, so a missing handler is reported by CreateStream itself.
        var stream = HandlerWrapperCache.GetStreamWrapper<TResponse>(request.GetType()).Handle(request, serviceProvider, cancellationToken);
        return MediatorTelemetry.IsEnabled ? ObserveStream(request.GetType(), stream, cancellationToken) : stream;
    }

    /// <summary>
    /// Invokes the notification's handlers sequentially in the given scope. The first failure stops the sequence and
    /// is returned through the task.
    /// </summary>
    public static Task PublishAndWait(INotification notification, IServiceProvider serviceProvider, bool continueOnCapturedContext, CancellationToken cancellationToken)
    {
        if (notification is null)
            return Task.FromException(new ArgumentNullException(nameof(notification)));

        try
        {
            var notificationType = notification.GetType();
            var wrapper = HandlerWrapperCache.GetNotificationWrapper(notificationType);
            var handlers = wrapper?.ResolveHandlers(serviceProvider) ?? Array.Empty<object>();

            if (MediatorTelemetry.IsEnabled)
                return PublishAndWaitObserved(wrapper, handlers, notification, notificationType, continueOnCapturedContext, cancellationToken);

            return handlers.Length switch
            {
                0 => Task.CompletedTask,
                1 => wrapper!.Handle(handlers[0], notification, cancellationToken),
                _ => InvokeSequentially(wrapper!, handlers, notification, continueOnCapturedContext, cancellationToken),
            };
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private static Task<TResponse> SendCore<TResponse>(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        try
        {
            return HandlerWrapperCache.GetRequestWrapper<TResponse>(request.GetType()).Handle(request, serviceProvider, cancellationToken);
        }
        catch (Exception ex)
        {
            // Surface synchronous failures (e.g. handler not found) through the task, as an async method would.
            return Task.FromException<TResponse>(ex);
        }
    }

    private static Task SendCommandCore<TRequest>(TRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        try
        {
            // The command type is known statically, so the handler is resolved without the wrapper cache.
            return CommandHandlerWrapper<TRequest>.Handle(request, serviceProvider, cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private static Task SendCommandCore(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        try
        {
            return HandlerWrapperCache.GetCommandWrapper(request.GetType()).Handle(request, serviceProvider, cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private static async Task InvokeSequentially(NotificationHandlerWrapper wrapper, object[] handlers, INotification notification, bool continueOnCapturedContext, CancellationToken cancellationToken)
    {
        // Sequential on purpose: handlers commonly share scoped services such as a DbContext, which are not thread-safe.
        foreach (var handler in handlers)
        {
            await wrapper.Handle(handler, notification, cancellationToken).ConfigureAwait(continueOnCapturedContext);
        }
    }

    // ---- Observed paths: only used while traces or metrics are being collected. ----
    // The lambdas live in these separate methods on purpose: a lambda capturing parameters allocates its closure at
    // method entry, so placing it in the public methods would cost an allocation even when telemetry is off.

    private static Task<TResponse> SendObserved<TResponse>(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => Observe(request.GetType(), "send", () => SendCore(request, serviceProvider, cancellationToken));

    private static Task SendCommandObserved<TRequest>(TRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        where TRequest : IRequest
        => Observe(typeof(TRequest), "send", () => SendCommandCore(request, serviceProvider, cancellationToken));

    private static Task SendCommandObserved(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => Observe(request.GetType(), "send", () => SendCommandCore(request, serviceProvider, cancellationToken));

    private static async Task<TResponse> Observe<TResponse>(Type messageType, string operation, Func<Task<TResponse>> invoke)
    {
        // The activity is current while the pipeline runs, so spans created by handlers become its children.
        using var activity = MediatorTelemetry.StartActivity(operation, messageType);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await invoke().ConfigureAwait(false);
            MediatorTelemetry.RecordRequest(messageType, start, null);
            return result;
        }
        catch (Exception ex)
        {
            MediatorTelemetry.SetError(activity, ex);
            MediatorTelemetry.RecordRequest(messageType, start, ex);
            throw;
        }
    }

    private static async Task Observe(Type messageType, string operation, Func<Task> invoke)
    {
        using var activity = MediatorTelemetry.StartActivity(operation, messageType);
        var start = Stopwatch.GetTimestamp();
        try
        {
            await invoke().ConfigureAwait(false);
            MediatorTelemetry.RecordRequest(messageType, start, null);
        }
        catch (Exception ex)
        {
            MediatorTelemetry.SetError(activity, ex);
            MediatorTelemetry.RecordRequest(messageType, start, ex);
            throw;
        }
    }

    private static async IAsyncEnumerable<TResponse> ObserveStream<TResponse>(Type messageType, IAsyncEnumerable<TResponse> source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The span covers the whole enumeration, from the first item to completion, failure or early exit.
        using var activity = MediatorTelemetry.StartActivity("stream", messageType);
        var start = Stopwatch.GetTimestamp();
        Exception? failure = null;
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    MediatorTelemetry.SetError(activity, ex);
                    throw;
                }

                if (!hasNext) break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            MediatorTelemetry.RecordRequest(messageType, start, failure);
        }
    }

    private static async Task PublishAndWaitObserved(NotificationHandlerWrapper? wrapper, object[] handlers, INotification notification, Type notificationType, bool continueOnCapturedContext, CancellationToken cancellationToken)
    {
        MediatorTelemetry.RecordPublished(notificationType, "awaited");
        using var activity = MediatorTelemetry.StartActivity("publish", notificationType);
        activity?.SetTag(MediatorTelemetry.PublishModeTag, "awaited");

        try
        {
            foreach (var handler in handlers)
            {
                using var handlerActivity = MediatorTelemetry.StartActivity("handle", notificationType);
                handlerActivity?.SetTag(MediatorTelemetry.HandlerTypeTag, handler.GetType().FullName);
                var start = Stopwatch.GetTimestamp();
                try
                {
                    await wrapper!.Handle(handler, notification, cancellationToken).ConfigureAwait(continueOnCapturedContext);
                    MediatorTelemetry.RecordHandler(notificationType, handler.GetType(), start, null);
                }
                catch (Exception ex)
                {
                    MediatorTelemetry.SetError(handlerActivity, ex);
                    MediatorTelemetry.RecordHandler(notificationType, handler.GetType(), start, ex);
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            MediatorTelemetry.SetError(activity, ex);
            throw;
        }
    }
}
