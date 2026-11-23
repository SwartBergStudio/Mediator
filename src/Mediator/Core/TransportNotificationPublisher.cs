using System.Diagnostics;
using Mediator.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// Publishes notifications through an <see cref="INotificationTransport"/> (a message broker) instead of the in-process
/// background queue. Registered by <c>AddNotificationTransport</c>, in place of <see cref="NotificationPublisher"/>.
/// </summary>
/// <remarks>
/// Publish completes when the broker accepted the message, and faults when it did not: the broker is the durable store,
/// so there is no local queue or persistence to fall back to, and a lost notification must surface to the caller.
/// </remarks>
internal sealed class TransportNotificationPublisher : INotificationPublisher
{
    private readonly INotificationTransport _transport;
    private readonly IServiceProvider _services;
    private readonly ILogger<TransportNotificationPublisher> _logger;
    private readonly bool _continueOnCapturedContext;

    public TransportNotificationPublisher(
        INotificationTransport transport,
        IServiceProvider services,
        ILogger<TransportNotificationPublisher> logger,
        IOptions<MediatorOptions> options)
    {
        _transport = transport;
        _services = services;
        _logger = logger;
        _continueOnCapturedContext = !options.Value.UseConfigureAwaitGlobally;

        if (options.Value.EnablePersistence)
        {
            _logger.LogWarning("EnablePersistence is ignored: notifications are published through {Transport}, which stores them durably.", transport.GetType().Name);
        }
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        // Thrown inside the async method, so it surfaces through the task, like the background publisher.
        if (notification is null)
            throw new ArgumentNullException(nameof(notification));

        var notificationType = notification.GetType();
        MediatorTelemetry.RecordPublished(notificationType, "transport");
        using var activity = MediatorTelemetry.StartActivity("publish", notificationType, kind: ActivityKind.Producer);
        activity?.SetTag(MediatorTelemetry.PublishModeTag, "transport");

        try
        {
            var serializer = _services.GetService<INotificationSerializer>()
                ?? throw new InvalidOperationException("No INotificationSerializer is registered. AddNotificationTransport registers JsonNotificationSerializer unless one was registered before it.");

            var payload = serializer.Serialize(notification, notificationType);
            if (string.IsNullOrEmpty(payload))
                throw new InvalidOperationException($"{serializer.GetType().Name} returned no payload for notification {notificationType.Name}.");

            // The handler spans on the consuming side become children of this span (or of the caller's, without one).
            var trace = Activity.Current;
            var envelope = new NotificationEnvelope(Guid.NewGuid().ToString("N"), notificationType.AssemblyQualifiedName!, payload)
            {
                CreatedAt = DateTimeOffset.UtcNow,
                TraceParent = trace?.IdFormat == ActivityIdFormat.W3C ? trace.Id : null,
                TraceState = trace?.TraceStateString,
            };

            await _transport.SendAsync(envelope, cancellationToken).ConfigureAwait(_continueOnCapturedContext);
        }
        catch (Exception ex)
        {
            MediatorTelemetry.SetError(activity, ex);
            _logger.LogError(ex, "Failed to publish notification {NotificationType} through {Transport}", notificationType.Name, _transport.GetType().Name);
            throw;
        }
    }
}
