using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;

namespace Mediator.AzureServiceBus;

/// <summary>
/// The <see cref="ServiceBusClient"/> the transport and processor share. Created on first use, so an app that never
/// publishes or processes never connects. Kept separate from any <see cref="ServiceBusClient"/> the app registers itself.
/// </summary>
internal sealed class ServiceBusClientProvider : IAsyncDisposable
{
    private readonly Lazy<ServiceBusClient> _client;
    private bool _owned;

    public ServiceBusClientProvider(IServiceProvider services, IOptions<AzureServiceBusNotificationOptions> options)
    {
        _client = new Lazy<ServiceBusClient>(() => Create(services, options.Value));
    }

    public ServiceBusClient Client => _client.Value;

    private ServiceBusClient Create(IServiceProvider services, AzureServiceBusNotificationOptions o)
    {
        if (o.ClientFactory != null)
            return o.ClientFactory(services) ?? throw new InvalidOperationException($"{nameof(AzureServiceBusNotificationOptions.ClientFactory)} returned null.");

        _owned = true;
        var clientOptions = o.ClientOptions ?? new ServiceBusClientOptions();
        if (!string.IsNullOrWhiteSpace(o.ConnectionString))
            return new ServiceBusClient(o.ConnectionString, clientOptions);

        if (!string.IsNullOrWhiteSpace(o.FullyQualifiedNamespace) && o.Credential != null)
            return new ServiceBusClient(o.FullyQualifiedNamespace, o.Credential, clientOptions);

        _owned = false;
        throw new InvalidOperationException(
            $"Azure Service Bus is not configured: set {nameof(AzureServiceBusNotificationOptions.ConnectionString)}, " +
            $"or {nameof(AzureServiceBusNotificationOptions.FullyQualifiedNamespace)} and {nameof(AzureServiceBusNotificationOptions.Credential)}, " +
            $"or {nameof(AzureServiceBusNotificationOptions.ClientFactory)}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_owned && _client.IsValueCreated)
            await _client.Value.DisposeAsync().ConfigureAwait(false);
    }
}
