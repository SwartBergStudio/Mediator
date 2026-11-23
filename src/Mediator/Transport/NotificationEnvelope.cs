namespace Mediator.Transport;

/// <summary>
/// A published notification as it travels through an <see cref="INotificationTransport"/>: the serialized notification
/// plus what the consuming side needs to dispatch, retry and trace it. It holds no transport-specific data, so any
/// broker can carry it (as a message body and properties).
/// </summary>
public sealed class NotificationEnvelope
{
    /// <summary>Creates an envelope.</summary>
    /// <param name="messageId">Unique id of this message. Brokers can use it for duplicate detection.</param>
    /// <param name="notificationType">Assembly-qualified name of the notification type.</param>
    /// <param name="payload">The notification, serialized with the registered <see cref="Serialization.INotificationSerializer"/>.</param>
    public NotificationEnvelope(string messageId, string notificationType, string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentException.ThrowIfNullOrEmpty(notificationType);
        ArgumentNullException.ThrowIfNull(payload);

        MessageId = messageId;
        NotificationType = notificationType;
        Payload = payload;
    }

    /// <summary>Unique id of this message. A retry gets a new id, so broker duplicate detection never drops it.</summary>
    public string MessageId { get; }

    /// <summary>Assembly-qualified name of the notification type.</summary>
    public string NotificationType { get; }

    /// <summary>The notification, serialized with the registered <see cref="Serialization.INotificationSerializer"/>.</summary>
    public string Payload { get; }

    /// <summary>
    /// When set, only this handler runs: the message retries one handler that failed while the others succeeded.
    /// The format is <c>"Namespace.Type, AssemblyName"</c>, without a version; <c>null</c> runs every handler.
    /// </summary>
    public string? TargetHandlerType { get; init; }

    /// <summary>How many times handling this notification has failed before. 0 for a newly published notification.</summary>
    public int AttemptCount { get; init; }

    /// <summary>When the notification was first published (UTC). Kept unchanged across retries.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The earliest time the message should be delivered, for retries with backoff. <c>null</c> delivers at once.
    /// Transports should schedule the message for this time (for example Azure Service Bus <c>ScheduledEnqueueTime</c>).
    /// </summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>W3C <c>traceparent</c> of the publish span, so handler spans join the publisher's trace.</summary>
    public string? TraceParent { get; init; }

    /// <summary>W3C <c>tracestate</c> of the publish span.</summary>
    public string? TraceState { get; init; }

    /// <summary>
    /// A new envelope that retries this notification: a new <see cref="MessageId"/>, <see cref="AttemptCount"/> plus one,
    /// and the same payload, creation time and trace.
    /// </summary>
    /// <param name="targetHandlerType">The one handler to retry, or <c>null</c> to keep this envelope's target.</param>
    /// <param name="notBefore">The earliest time to deliver the retry.</param>
    public NotificationEnvelope ForRetry(string? targetHandlerType, DateTimeOffset? notBefore) =>
        new(Guid.NewGuid().ToString("N"), NotificationType, Payload)
        {
            TargetHandlerType = targetHandlerType ?? TargetHandlerType,
            AttemptCount = AttemptCount + 1,
            CreatedAt = CreatedAt,
            NotBefore = notBefore,
            TraceParent = TraceParent,
            TraceState = TraceState,
        };
}
