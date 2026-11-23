using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.AzureServiceBus;

/// <summary>
/// Receives notifications from the queue (or the topic subscription) while the host runs, and hands each message to
/// <see cref="ServiceBusNotificationHandler"/>.
/// </summary>
internal sealed class AzureServiceBusNotificationProcessor : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClientProvider _clients;
    private readonly ServiceBusNotificationHandler _handler;
    private readonly AzureServiceBusNotificationOptions _options;
    private readonly MediatorOptions _mediatorOptions;
    private readonly ILogger<AzureServiceBusNotificationProcessor> _logger;
    private ServiceBusProcessor? _processor;

    public AzureServiceBusNotificationProcessor(
        ServiceBusClientProvider clients,
        ServiceBusNotificationHandler handler,
        IOptions<AzureServiceBusNotificationOptions> options,
        IOptions<MediatorOptions> mediatorOptions,
        ILogger<AzureServiceBusNotificationProcessor> logger)
    {
        _clients = clients;
        _handler = handler;
        _options = options.Value;
        _mediatorOptions = mediatorOptions.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableProcessor)
        {
            _logger.LogInformation("Azure Service Bus processor is disabled; this app only publishes notifications");
            return;
        }

        var entityPath = _options.RequireEntityPath();
        var processorOptions = new ServiceBusProcessorOptions
        {
            // Settled by the handler: completing a message whose handlers failed would lose it.
            AutoCompleteMessages = false,
            MaxConcurrentCalls = Math.Max(1, _options.MaxConcurrentCalls),
            PrefetchCount = Math.Max(0, _options.PrefetchCount),
            MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
        };

        _processor = _options.SubscriptionName is { Length: > 0 } subscription
            ? _clients.Client.CreateProcessor(entityPath, subscription, processorOptions)
            : _clients.Client.CreateProcessor(entityPath, processorOptions);
        _processor.ProcessMessageAsync += args => _handler.HandleAsync(args.Message, new ProcessMessageEventArgsSettler(args), args.CancellationToken);
        _processor.ProcessErrorAsync += OnErrorAsync;

        await _processor.StartProcessingAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Receiving notifications from Azure Service Bus {EntityPath}{Subscription}", entityPath,
            _options.SubscriptionName is null ? "" : $"/subscriptions/{_options.SubscriptionName}");
    }

    /// <summary>
    /// Stops receiving and waits for the messages being handled, for up to <see cref="MediatorOptions.ShutdownTimeout"/>.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor == null) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_mediatorOptions.ShutdownTimeout != Timeout.InfiniteTimeSpan)
            timeout.CancelAfter(_mediatorOptions.ShutdownTimeout < TimeSpan.Zero ? TimeSpan.Zero : _mediatorOptions.ShutdownTimeout);

        try
        {
            await _processor.StopProcessingAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Safe: messages still being handled keep their lock until it expires; Service Bus then delivers them again.
            _logger.LogWarning("Stopped waiting for Azure Service Bus handlers after the shutdown timeout ({ShutdownTimeout}); unfinished messages are delivered again", _mediatorOptions.ShutdownTimeout);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor != null)
            await _processor.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        // The processor recovers by itself (reconnects, retries receiving); this is for visibility only.
        _logger.LogError(args.Exception, "Azure Service Bus processor error ({ErrorSource}) on {EntityPath}", args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }
}
