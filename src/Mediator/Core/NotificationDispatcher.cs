using System.Diagnostics;
using Mediator.Transport;
using Microsoft.Extensions.Logging;

namespace Mediator.Core;

/// <summary>
/// The consumer side of an <see cref="INotificationTransport"/>: turns a received envelope back into a notification and
/// runs its handlers with <see cref="NotificationHandlerInvoker"/>.
/// </summary>
internal sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NotificationDispatcher> _logger;
    private readonly NotificationHandlerInvoker _invoker;

    public NotificationDispatcher(IServiceProvider services, IScopeProvider scopeProvider, ILogger<NotificationDispatcher> logger)
    {
        _services = services;
        _logger = logger;
        _invoker = new NotificationHandlerInvoker(scopeProvider, logger);
    }

    public async Task<NotificationDispatchResult> DispatchAsync(NotificationEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // Resolved per call rather than in the constructor: a transport may be registered before the serializer is.
        var serializer = _services.GetService<INotificationSerializer>()
            ?? throw new InvalidOperationException("No INotificationSerializer is registered. AddNotificationTransport registers JsonNotificationSerializer unless one was registered before it.");

        var notificationType = NotificationTypeResolver.Resolve(envelope.NotificationType);
        if (notificationType == null)
            return Poison(envelope, new InvalidOperationException($"Notification type '{envelope.NotificationType}' is not known to this application."));

        object? notification;
        try
        {
            notification = serializer.Deserialize(envelope.Payload, notificationType);
        }
        catch (Exception ex)
        {
            return Poison(envelope, new InvalidOperationException($"The payload of message {envelope.MessageId} could not be deserialized as {notificationType.Name}.", ex));
        }

        if (notification == null)
            return Poison(envelope, new InvalidOperationException($"The payload of message {envelope.MessageId} could not be deserialized as {notificationType.Name}."));

        ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, out var parent);

        var result = await _invoker.DispatchAsync(notificationType, notification, envelope.TargetHandlerType, parent, cancellationToken).ConfigureAwait(false);
        if (result.Error != null)
            return NotificationDispatchResult.Failed(result.Error);

        if (result.Failures == null)
            return NotificationDispatchResult.Success(result.HandlerCount);

        var failures = new NotificationHandlerFailure[result.Failures.Count];
        for (var i = 0; i < failures.Length; i++)
        {
            var failure = result.Failures[i];
            failures[i] = new NotificationHandlerFailure(NotificationHandlerInvoker.HandlerKey(failure.HandlerType), failure.Error);
        }
        return NotificationDispatchResult.HandlersFailed(result.HandlerCount, failures);
    }

    private NotificationDispatchResult Poison(NotificationEnvelope envelope, Exception error)
    {
        _logger.LogError(error, "Message {MessageId} with notification type {NotificationType} cannot be handled", envelope.MessageId, envelope.NotificationType);
        return NotificationDispatchResult.Poison(error);
    }
}
