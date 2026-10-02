using Mediator.Core.Wrappers;

namespace Mediator.Core;

/// <summary>
/// Request, command and stream dispatch shared by <see cref="Mediator"/> and the dispatcher services.
/// </summary>
/// <remarks>
/// Each call looks up the cached, strongly-typed dispatcher for the message type and returns the pipeline's task (or
/// stream) directly. There is deliberately no async wrapper or logging here: these handlers run inside the caller's
/// await, so exceptions reach the caller (and its host's logging) unchanged and no extra state machine is allocated.
/// Background notifications, which nobody awaits, are logged by <see cref="NotificationPublisher"/> instead.
/// </remarks>
internal static class Dispatch
{
    public static Task<TResponse> Send<TResponse>(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        if (request is null)
            return Task.FromException<TResponse>(new ArgumentNullException(nameof(request)));

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

    public static Task SendCommand<TRequest>(TRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
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

    public static Task SendCommand(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        if (request is null)
            return Task.FromException(new ArgumentNullException(nameof(request)));

        try
        {
            return HandlerWrapperCache.GetCommandWrapper(request.GetType()).Handle(request, serviceProvider, cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public static IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return HandlerWrapperCache.GetStreamWrapper<TResponse>(request.GetType()).Handle(request, serviceProvider, cancellationToken);
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
            var wrapper = HandlerWrapperCache.GetNotificationWrapper(notification.GetType());
            if (wrapper is null)
                return Task.CompletedTask;

            var handlers = wrapper.ResolveHandlers(serviceProvider);
            return handlers.Length switch
            {
                0 => Task.CompletedTask,
                1 => wrapper.Handle(handlers[0], notification, cancellationToken),
                _ => InvokeSequentially(wrapper, handlers, notification, continueOnCapturedContext, cancellationToken),
            };
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
}
