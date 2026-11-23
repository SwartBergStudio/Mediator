namespace Mediator.Transport;

/// <summary>
/// The outcome of <see cref="INotificationDispatcher.DispatchAsync"/>, used by a transport to settle the message.
/// </summary>
public sealed class NotificationDispatchResult
{
    private static readonly IReadOnlyList<NotificationHandlerFailure> s_noFailures = Array.Empty<NotificationHandlerFailure>();

    private NotificationDispatchResult(int handlerCount, IReadOnlyList<NotificationHandlerFailure> failedHandlers, Exception? error, bool isPoison)
    {
        HandlerCount = handlerCount;
        FailedHandlers = failedHandlers;
        Error = error;
        IsPoison = isPoison;
    }

    /// <summary>Every handler that ran succeeded (or the notification has no handlers).</summary>
    public bool Succeeded => Error == null && FailedHandlers.Count == 0;

    /// <summary>
    /// The message can never be handled: its notification type is unknown here or its payload cannot be deserialized.
    /// Retrying will not help; dead-letter it. <see cref="Error"/> says why.
    /// </summary>
    public bool IsPoison { get; }

    /// <summary>
    /// Set when no handler could run (poison message, or the DI scope or handlers could not be created). Retry the whole
    /// message unless <see cref="IsPoison"/>.
    /// </summary>
    public Exception? Error { get; }

    /// <summary>How many handlers ran.</summary>
    public int HandlerCount { get; }

    /// <summary>
    /// The handlers that threw. When fewer than <see cref="HandlerCount"/> failed, retry only these (one message per handler
    /// with <see cref="NotificationEnvelope.TargetHandlerType"/>), so the handlers that succeeded do not run again.
    /// </summary>
    public IReadOnlyList<NotificationHandlerFailure> FailedHandlers { get; }

    internal static NotificationDispatchResult Success(int handlerCount) => new(handlerCount, s_noFailures, null, false);

    internal static NotificationDispatchResult HandlersFailed(int handlerCount, IReadOnlyList<NotificationHandlerFailure> failures) => new(handlerCount, failures, null, false);

    internal static NotificationDispatchResult Failed(Exception error) => new(0, s_noFailures, error, false);

    internal static NotificationDispatchResult Poison(Exception error) => new(0, s_noFailures, error, true);
}

/// <summary>A handler that threw while handling a notification.</summary>
public sealed class NotificationHandlerFailure
{
    /// <summary>Creates a failure.</summary>
    public NotificationHandlerFailure(string handlerType, Exception error)
    {
        HandlerType = handlerType;
        Error = error;
    }

    /// <summary>
    /// The handler, as <c>"Namespace.Type, AssemblyName"</c>: the value to put in
    /// <see cref="NotificationEnvelope.TargetHandlerType"/> to retry only this handler.
    /// </summary>
    public string HandlerType { get; }

    /// <summary>The exception the handler threw.</summary>
    public Exception Error { get; }
}
