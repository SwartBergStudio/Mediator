using Microsoft.Extensions.Hosting;

namespace Mediator.Core;

/// <summary>
/// Ties the background notification publisher to the host's lifetime.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Start:</b> creates the publisher at startup instead of on the first <c>Publish</c>, so notifications left in
/// storage by the previous run are recovered right away, even if nothing is published for a while.</item>
/// <item><b>Stop:</b> lets queued and running notifications finish (up to <see cref="MediatorOptions.ShutdownTimeout"/>).
/// The host stops hosted services before it disposes the service provider; once disposal has started, no DI scope can be
/// created for a queued notification any more.</item>
/// </list>
/// Apps without a generic host still work: the publisher is then created on first use and only lets running handlers
/// finish when the service provider is disposed.
/// </remarks>
internal sealed class NotificationPublisherLifetime(IServiceProvider services) : IHostedService
{
    private NotificationPublisher? _publisher;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A custom INotificationPublisher manages its own lifetime.
        _publisher = services.GetService<INotificationPublisher>() as NotificationPublisher;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => _publisher?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}
