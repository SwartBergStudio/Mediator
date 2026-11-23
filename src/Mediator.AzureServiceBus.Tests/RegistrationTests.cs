using Mediator.Transport;
using Microsoft.Extensions.Hosting;

namespace Mediator.AzureServiceBus.Tests;

public class RegistrationTests
{
    // Not a real namespace or key: creating the client does not connect.
    private const string PlaceholderConnectionString = "Endpoint=sb://placeholder.servicebus.windows.net/;SharedAccessKeyName=placeholder;SharedAccessKey=cGxhY2Vob2xkZXI=";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddMediatorAzureServiceBus_ShouldPublishThroughServiceBus_AndRegisterTheProcessor(bool serviceBusFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (serviceBusFirst) AddServiceBus(services);
        services.AddMediator();
        if (!serviceBusFirst) AddServiceBus(services);
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<INotificationPublisher>().GetType().Name.Should().Be("TransportNotificationPublisher");
        provider.GetRequiredService<INotificationTransport>().Should().BeSameAs(provider.GetRequiredService<AzureServiceBusNotificationTransport>(),
            "publishing and retries share one sender");
        provider.GetServices<IHostedService>().Should().ContainSingle(s => s is AzureServiceBusNotificationProcessor);
        provider.GetRequiredService<ServiceBusClientProvider>().Client.FullyQualifiedNamespace.Should().Be("placeholder.servicebus.windows.net");
    }

    [Fact]
    public async Task WithoutConnectionSettings_PublishShouldFailWithAClearMessage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddMediatorAzureServiceBus(o => o.EntityPath = "notifications");
        await using var provider = services.BuildServiceProvider();

        var publish = () => provider.GetRequiredService<IMediator>().Publish(new Ping());

        await publish.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ConnectionString*FullyQualifiedNamespace*ClientFactory*");
    }

    [Fact]
    public async Task WithoutAnEntityPath_TheProcessorShouldFailToStartWithAClearMessage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddMediatorAzureServiceBus(o => o.ConnectionString = PlaceholderConnectionString);
        await using var provider = services.BuildServiceProvider();
        var processor = provider.GetServices<IHostedService>().OfType<AzureServiceBusNotificationProcessor>().Single();

        var start = () => processor.StartAsync(default);

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*EntityPath*");
    }

    private static void AddServiceBus(IServiceCollection services) =>
        services.AddMediatorAzureServiceBus(o =>
        {
            o.ConnectionString = PlaceholderConnectionString;
            o.EntityPath = "notifications";
        });
}
