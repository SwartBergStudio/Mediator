namespace Mediator.Transport;

/// <summary>
/// Carries published notifications to a message broker (Azure Service Bus, RabbitMQ, ...) instead of the in-process
/// background queue. Register one with
/// <see cref="MediatorServiceCollectionExtensions.AddNotificationTransport{TTransport}(IServiceCollection)"/>.
/// </summary>
/// <remarks>
/// This is the producer side. The consumer side receives messages from the broker, turns them back into a
/// <see cref="NotificationEnvelope"/> and passes them to <see cref="INotificationDispatcher"/>, which runs the handlers.
/// </remarks>
public interface INotificationTransport
{
    /// <summary>
    /// Sends the envelope to the broker. <c>IMediator.Publish</c> completes when this task does, and faults if it faults,
    /// so a notification the broker did not accept is never reported as published.
    /// </summary>
    /// <param name="envelope">The notification to send. Honour <see cref="NotificationEnvelope.NotBefore"/> when set.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task SendAsync(NotificationEnvelope envelope, CancellationToken cancellationToken = default);
}
