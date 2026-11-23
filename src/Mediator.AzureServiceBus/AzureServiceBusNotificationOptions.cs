using Azure.Core;
using Azure.Messaging.ServiceBus;

namespace Mediator.AzureServiceBus;

/// <summary>
/// Where and how notifications are sent to and received from Azure Service Bus.
/// </summary>
/// <remarks>
/// Connect with one of: <see cref="ConnectionString"/>; <see cref="FullyQualifiedNamespace"/> and <see cref="Credential"/>
/// (for example <c>new DefaultAzureCredential()</c> from Azure.Identity, for managed identity); or
/// <see cref="ClientFactory"/> to use a <see cref="ServiceBusClient"/> the app already has.
/// Retry timing and the number of attempts come from <see cref="MediatorOptions"/>
/// (<see cref="MediatorOptions.InitialRetryDelay"/>, <see cref="MediatorOptions.RetryDelayMultiplier"/>,
/// <see cref="MediatorOptions.MaxRetryAttempts"/>), as for in-process notifications.
/// </remarks>
public sealed class AzureServiceBusNotificationOptions
{
    /// <summary>Service Bus connection string (shared access key).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Namespace host name, such as <c>contoso.servicebus.windows.net</c>. Used with <see cref="Credential"/>.</summary>
    public string? FullyQualifiedNamespace { get; set; }

    /// <summary>Microsoft Entra ID credential for <see cref="FullyQualifiedNamespace"/>.</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>
    /// Supplies the client instead of creating one from the settings above. A client from the factory is not disposed by
    /// the mediator; its owner disposes it.
    /// </summary>
    public Func<IServiceProvider, ServiceBusClient>? ClientFactory { get; set; }

    /// <summary>Options for the client the mediator creates (transport type, retry policy, ...).</summary>
    public ServiceBusClientOptions? ClientOptions { get; set; }

    /// <summary>The queue, or the topic, notifications are sent to. Required.</summary>
    public string EntityPath { get; set; } = string.Empty;

    /// <summary>
    /// The subscription this app receives from when <see cref="EntityPath"/> is a topic; <c>null</c> for a queue.
    /// </summary>
    /// <remarks>
    /// Each service that handles the notifications has a subscription of its own. Retries are sent to the topic with the
    /// <c>mediator-subscription</c> property set to this name, and other subscriptions complete them without running
    /// handlers. To keep that traffic off them, give each subscription the SQL filter
    /// <c>[mediator-subscription] IS NULL OR [mediator-subscription] = '&lt;its name&gt;'</c>.
    /// </remarks>
    public string? SubscriptionName { get; set; }

    /// <summary>
    /// Whether this app receives and handles notifications. Set to <c>false</c> in apps that only publish.
    /// </summary>
    public bool EnableProcessor { get; set; } = true;

    /// <summary>How many messages are handled at the same time.</summary>
    public int MaxConcurrentCalls { get; set; } = Environment.ProcessorCount;

    /// <summary>How many messages the processor fetches ahead. 0 turns prefetching off.</summary>
    public int PrefetchCount { get; set; }

    /// <summary>How long the processor keeps renewing a message's lock while its handlers run.</summary>
    public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest delay before a retry, however many attempts have failed. Defaults to one day.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromDays(1);

    internal string RequireEntityPath() =>
        string.IsNullOrWhiteSpace(EntityPath)
            ? throw new InvalidOperationException($"{nameof(AzureServiceBusNotificationOptions)}.{nameof(EntityPath)} must name the queue or topic notifications are sent to.")
            : EntityPath;
}
