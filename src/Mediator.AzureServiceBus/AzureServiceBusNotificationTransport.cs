using Azure.Messaging.ServiceBus;
using Mediator.Transport;
using Microsoft.Extensions.Options;

namespace Mediator.AzureServiceBus;

/// <summary>
/// Sends published notifications (and retries) to the configured queue or topic.
/// </summary>
internal sealed class AzureServiceBusNotificationTransport : INotificationTransport, IAsyncDisposable
{
    private readonly Lazy<ServiceBusSender> _sender;
    private int _disposed;

    public AzureServiceBusNotificationTransport(ServiceBusClientProvider clients, IOptions<AzureServiceBusNotificationOptions> options)
    {
        // Created on first send, so the configuration is only needed by apps that publish.
        _sender = new Lazy<ServiceBusSender>(() => clients.Client.CreateSender(options.Value.RequireEntityPath()));
    }

    /// <summary>For tests: sends through the given sender.</summary>
    internal AzureServiceBusNotificationTransport(ServiceBusSender sender)
    {
        _sender = new Lazy<ServiceBusSender>(sender);
    }

    public Task SendAsync(NotificationEnvelope envelope, CancellationToken cancellationToken = default)
        => SendAsync(envelope, retryForSubscription: null, cancellationToken);

    /// <param name="envelope">The notification.</param>
    /// <param name="retryForSubscription">For a retry on a topic: the only subscription that should handle it.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public Task SendAsync(NotificationEnvelope envelope, string? retryForSubscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return _sender.Value.SendMessageAsync(ServiceBusMessageMapper.ToMessage(envelope, retryForSubscription), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Registered both as itself and as INotificationTransport, so the container can dispose it twice.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_sender.IsValueCreated)
            await _sender.Value.DisposeAsync().ConfigureAwait(false);
    }
}
