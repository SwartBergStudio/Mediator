using Azure.Messaging.ServiceBus;
using Mediator.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.AzureServiceBus;

/// <summary>
/// Handles one received message: runs its handlers through <see cref="INotificationDispatcher"/>, then settles it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>All handlers succeeded:</b> complete.</item>
/// <item><b>Poison</b> (unknown type, payload that can't be deserialized, not a mediator message): dead-letter. Retrying
/// can never succeed.</item>
/// <item><b>Some handlers failed:</b> send one scheduled retry per failed handler (targeting only it, so the handlers that
/// succeeded don't run again), then complete. All failed, or nothing could run: one retry of the whole message.</item>
/// <item><b>Out of attempts</b> (<see cref="MediatorOptions.MaxRetryAttempts"/>): dead-letter, with the failed handlers in
/// the <c>mediator-failed-handlers</c> property. The message stays in the dead-letter queue for inspection and
/// resubmission; nothing is dropped.</item>
/// <item><b>The retry could not be sent, or handling threw:</b> abandon, so the broker redelivers the message (and
/// dead-letters it after the entity's MaxDeliveryCount).</item>
/// </list>
/// Delivery is at least once: a crash between sending the retries and completing the message runs handlers again.
/// </remarks>
internal sealed class ServiceBusNotificationHandler
{
    public const string InvalidMessageReason = "MediatorInvalidMessage";
    public const string PoisonReason = "MediatorPoisonMessage";
    public const string MaxRetriesReason = "MediatorMaxRetriesExceeded";

    // Service Bus limits the dead-letter description; keep it well inside.
    private const int MaxDescriptionLength = 1024;

    private readonly INotificationDispatcher _dispatcher;
    private readonly AzureServiceBusNotificationTransport _transport;
    private readonly MediatorOptions _mediatorOptions;
    private readonly AzureServiceBusNotificationOptions _options;
    private readonly ILogger<ServiceBusNotificationHandler> _logger;

    public ServiceBusNotificationHandler(
        INotificationDispatcher dispatcher,
        AzureServiceBusNotificationTransport transport,
        IOptions<MediatorOptions> mediatorOptions,
        IOptions<AzureServiceBusNotificationOptions> options,
        ILogger<ServiceBusNotificationHandler> logger)
    {
        _dispatcher = dispatcher;
        _transport = transport;
        _mediatorOptions = mediatorOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleAsync(ServiceBusReceivedMessage message, IMessageSettler settler, CancellationToken cancellationToken)
    {
        try
        {
            await HandleCoreAsync(message, settler, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling message {MessageId} failed; abandoning it so Service Bus delivers it again", message.MessageId);
            try
            {
                // No token: the lock must be released even while the processor is stopping.
                await settler.AbandonAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception abandonError)
            {
                // Safe to swallow: the message stays locked until its lock expires, then Service Bus delivers it again
                // (and dead-letters it after the entity's MaxDeliveryCount). Nothing is lost.
                _logger.LogWarning(abandonError, "Abandoning message {MessageId} failed; it is delivered again when its lock expires", message.MessageId);
            }
        }
    }

    private async Task HandleCoreAsync(ServiceBusReceivedMessage message, IMessageSettler settler, CancellationToken cancellationToken)
    {
        // A retry sent to a topic for another subscription: that subscription handles it.
        var retryFor = ServiceBusMessageMapper.RetryForSubscription(message);
        if (retryFor != null && !string.Equals(retryFor, _options.SubscriptionName, StringComparison.Ordinal))
        {
            await settler.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        NotificationEnvelope envelope;
        try
        {
            envelope = ServiceBusMessageMapper.FromMessage(message);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Dead-lettering message {MessageId}: it is not a mediator notification", message.MessageId);
            await settler.DeadLetterAsync(InvalidMessageReason, Truncate(ex.Message), null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await _dispatcher.DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await settler.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.IsPoison)
        {
            // The dispatcher has logged why.
            await settler.DeadLetterAsync(PoisonReason, Truncate(result.Error!.Message), null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await RetryOrDeadLetterAsync(envelope, result, settler, cancellationToken).ConfigureAwait(false);
    }

    private async Task RetryOrDeadLetterAsync(NotificationEnvelope envelope, NotificationDispatchResult result, IMessageSettler settler, CancellationToken cancellationToken)
    {
        var error = result.Error ?? result.FailedHandlers[0].Error;

        // Only some handlers failed: retry each of them alone. Otherwise retry the message as it is (all its handlers,
        // or the one it already targets).
        var partial = result.Error == null && result.FailedHandlers.Count < result.HandlerCount;
        var targets = partial
            ? result.FailedHandlers.Select(f => (string?)f.HandlerType).ToList()
            : new List<string?> { null };

        if (envelope.AttemptCount >= _mediatorOptions.MaxRetryAttempts)
        {
            var failed = result.FailedHandlers.Count > 0
                ? string.Join("; ", result.FailedHandlers.Select(f => f.HandlerType))
                : envelope.TargetHandlerType ?? "(all handlers)";
            _logger.LogWarning(error, "Max retry attempts ({MaxRetries}) reached for message {MessageId} ({NotificationType}); dead-lettering it. Failed: {FailedHandlers}",
                _mediatorOptions.MaxRetryAttempts, envelope.MessageId, envelope.NotificationType, failed);
            await settler.DeadLetterAsync(MaxRetriesReason, Truncate($"{error.GetType().Name}: {error.Message}"),
                new Dictionary<string, object> { [ServiceBusMessageMapper.FailedHandlersProperty] = failed }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var delay = RetryDelay(envelope.AttemptCount);
        DateTimeOffset? notBefore = delay > TimeSpan.Zero ? DateTimeOffset.UtcNow + delay : null;
        try
        {
            foreach (var target in targets)
            {
                var retry = envelope.ForRetry(target, notBefore);
                _logger.LogInformation("Scheduling retry {RetryMessageId} of message {MessageId} for {Handler}, attempt {Attempt}, after {Delay}",
                    retry.MessageId, envelope.MessageId, retry.TargetHandlerType ?? "all handlers", retry.AttemptCount, delay);
                await _transport.SendAsync(retry, _options.SubscriptionName, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Retries already sent may run again after redelivery: delivery is at least once.
            _logger.LogError(ex, "Could not schedule the retry of message {MessageId}; abandoning it so Service Bus delivers it again", envelope.MessageId);
            await settler.AbandonAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await settler.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="MediatorOptions.InitialRetryDelay"/> × <see cref="MediatorOptions.RetryDelayMultiplier"/>^attempts,
    /// capped at <see cref="AzureServiceBusNotificationOptions.MaxRetryDelay"/>.
    /// </summary>
    internal TimeSpan RetryDelay(int attemptCount)
    {
        var max = _options.MaxRetryDelay > TimeSpan.Zero ? _options.MaxRetryDelay : TimeSpan.Zero;
        var initial = Math.Max(0, _mediatorOptions.InitialRetryDelay.Ticks);
        var multiplier = _mediatorOptions.RetryDelayMultiplier > 0 ? _mediatorOptions.RetryDelayMultiplier : 1.0; // also NaN
        var ticks = initial * Math.Pow(multiplier, Math.Max(0, attemptCount));
        return ticks >= max.Ticks ? max : TimeSpan.FromTicks((long)ticks);
    }

    private static string Truncate(string text) => text.Length <= MaxDescriptionLength ? text : text[..MaxDescriptionLength];
}
