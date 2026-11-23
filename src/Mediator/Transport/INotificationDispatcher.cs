namespace Mediator.Transport;

/// <summary>
/// Runs the handlers of a notification received from a message broker. Registered by <c>AddMediator</c>; transports call
/// it from their consumer (message processor).
/// </summary>
/// <remarks>
/// Handlers run exactly as they do for in-process background notifications: concurrently, each in a DI scope of its own
/// (<see cref="IScopeProvider"/>), with the same spans and metrics. Handler exceptions are caught and reported in the
/// result, never thrown, so the transport decides how to settle the message (complete, retry, dead-letter).
/// </remarks>
public interface INotificationDispatcher
{
    /// <summary>
    /// Deserializes the notification and runs its handlers, or only <see cref="NotificationEnvelope.TargetHandlerType"/>.
    /// </summary>
    /// <param name="envelope">The received notification.</param>
    /// <param name="cancellationToken">Passed to the handlers; cancel it when the consumer stops.</param>
    Task<NotificationDispatchResult> DispatchAsync(NotificationEnvelope envelope, CancellationToken cancellationToken = default);
}
