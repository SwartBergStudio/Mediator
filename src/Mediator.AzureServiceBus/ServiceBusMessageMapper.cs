using Azure.Messaging.ServiceBus;
using Mediator.Transport;

namespace Mediator.AzureServiceBus;

/// <summary>
/// Maps a <see cref="NotificationEnvelope"/> to a Service Bus message and back: the payload is the body, everything else
/// is an application property (so topic subscriptions can filter on it).
/// </summary>
internal static class ServiceBusMessageMapper
{
    public const string ContentType = "application/json";
    public const string TypeProperty = "mediator-type";
    public const string TargetHandlerProperty = "mediator-target-handler";
    public const string AttemptProperty = "mediator-attempt";
    public const string CreatedAtProperty = "mediator-created-at";
    public const string SubscriptionProperty = "mediator-subscription";
    public const string FailedHandlersProperty = "mediator-failed-handlers";
    public const string TraceParentProperty = "traceparent";
    public const string TraceStateProperty = "tracestate";

    /// <param name="envelope">The notification.</param>
    /// <param name="retryForSubscription">For a retry on a topic: the only subscription that should handle it.</param>
    public static ServiceBusMessage ToMessage(NotificationEnvelope envelope, string? retryForSubscription = null)
    {
        var message = new ServiceBusMessage(BinaryData.FromString(envelope.Payload))
        {
            MessageId = envelope.MessageId,
            ContentType = ContentType,
            Subject = TypeFullName(envelope.NotificationType),
        };
        if (envelope.NotBefore is { } notBefore)
            message.ScheduledEnqueueTime = notBefore;

        var properties = message.ApplicationProperties;
        properties[TypeProperty] = envelope.NotificationType;
        properties[AttemptProperty] = envelope.AttemptCount;
        properties[CreatedAtProperty] = envelope.CreatedAt;
        if (envelope.TargetHandlerType != null) properties[TargetHandlerProperty] = envelope.TargetHandlerType;
        if (envelope.TraceParent != null) properties[TraceParentProperty] = envelope.TraceParent;
        if (envelope.TraceState != null) properties[TraceStateProperty] = envelope.TraceState;
        if (retryForSubscription != null) properties[SubscriptionProperty] = retryForSubscription;
        return message;
    }

    /// <exception cref="FormatException">The message was not sent by the mediator (it has no notification type).</exception>
    public static NotificationEnvelope FromMessage(ServiceBusReceivedMessage message)
    {
        var properties = message.ApplicationProperties;
        if (!properties.TryGetValue(TypeProperty, out var type) || type is not string typeName || typeName.Length == 0)
            throw new FormatException($"Message {message.MessageId} has no '{TypeProperty}' property, so it was not published by the mediator.");

        return new NotificationEnvelope(
            string.IsNullOrEmpty(message.MessageId) ? Guid.NewGuid().ToString("N") : message.MessageId,
            typeName,
            message.Body.ToString())
        {
            TargetHandlerType = properties.TryGetValue(TargetHandlerProperty, out var target) ? target as string : null,
            AttemptCount = properties.TryGetValue(AttemptProperty, out var attempt) ? ToInt(attempt) : 0,
            CreatedAt = properties.TryGetValue(CreatedAtProperty, out var created) ? ToDateTimeOffset(created) ?? message.EnqueuedTime : message.EnqueuedTime,
            TraceParent = properties.TryGetValue(TraceParentProperty, out var traceParent) ? traceParent as string : null,
            TraceState = properties.TryGetValue(TraceStateProperty, out var traceState) ? traceState as string : null,
        };
    }

    /// <summary>The subscription a retry on a topic is meant for, or <c>null</c> when it is for every receiver.</summary>
    public static string? RetryForSubscription(ServiceBusReceivedMessage message) =>
        message.ApplicationProperties.TryGetValue(SubscriptionProperty, out var value) ? value as string : null;

    private static string TypeFullName(string assemblyQualifiedName)
    {
        var comma = assemblyQualifiedName.IndexOf(',');
        return comma < 0 ? assemblyQualifiedName : assemblyQualifiedName[..comma];
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)Math.Clamp(l, 0, int.MaxValue),
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => 0,
    };

    // AMQP carries timestamps as DateTime; some tools write strings.
    private static DateTimeOffset? ToDateTimeOffset(object? value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
        string s when DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
        _ => null,
    };
}
