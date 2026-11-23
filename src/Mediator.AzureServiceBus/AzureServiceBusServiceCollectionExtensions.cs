using Mediator.AzureServiceBus;
using Mediator.Transport;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Mediator;

/// <summary>
/// Registers Azure Service Bus as the transport for published notifications.
/// </summary>
public static class AzureServiceBusServiceCollectionExtensions
{
    /// <summary>
    /// <c>IMediator.Publish</c> sends notifications to Azure Service Bus, and (unless
    /// <see cref="AzureServiceBusNotificationOptions.EnableProcessor"/> is off) a hosted processor receives them and runs
    /// their handlers. Use together with <c>AddMediator</c>, in either order.
    /// </summary>
    /// <remarks>
    /// Notifications are serialized with the registered <c>INotificationSerializer</c> (JSON with reflection by default).
    /// For Native AOT, register a <c>JsonNotificationSerializer</c> built from a source-generated <c>JsonSerializerContext</c>
    /// before calling this. Handlers must be idempotent: delivery is at least once.
    /// </remarks>
    public static IServiceCollection AddMediatorAzureServiceBus(this IServiceCollection services, Action<AzureServiceBusNotificationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddSingleton<ServiceBusClientProvider>();
        services.TryAddSingleton<AzureServiceBusNotificationTransport>();
        services.AddNotificationTransport<AzureServiceBusNotificationTransport>();
        // One sender: the publisher and the retry path share the transport instance.
        services.Replace(ServiceDescriptor.Singleton<INotificationTransport>(static sp => sp.GetRequiredService<AzureServiceBusNotificationTransport>()));
        services.TryAddSingleton<ServiceBusNotificationHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AzureServiceBusNotificationProcessor>());
        return services;
    }
}
