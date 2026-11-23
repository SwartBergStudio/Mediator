using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mediator.AzureServiceBus.Tests;

/// <summary>
/// Against a real Service Bus namespace or the Service Bus emulator. Skipped unless <c>SERVICEBUS_CONNECTION</c> is set;
/// <c>SERVICEBUS_QUEUE</c> names an existing queue (default <c>mediator-tests</c>) that nothing else reads from.
/// </summary>
public class ServiceBusIntegrationTests
{
    [SkippableFact]
    public async Task Publish_ShouldBeHandledFromTheQueue_AndAFailedHandlerRetriedAlone()
    {
        var connection = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connection), "SERVICEBUS_CONNECTION is not set");
        var queue = Environment.GetEnvironmentVariable("SERVICEBUS_QUEUE") is { Length: > 0 } q ? q : "mediator-tests";

        var recorder = new Recorder();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(recorder);
        builder.Services.AddSingleton<INotificationSerializer>(new JsonNotificationSerializer());
        builder.Services.AddTransient<INotificationHandler<Ping>, PingHandlerA>();
        builder.Services.AddTransient<INotificationHandler<Ping>, FailsOnceHandler>();
        builder.Services.AddMediator(o =>
        {
            o.InitialRetryDelay = TimeSpan.Zero; // retry at once
            o.MaxRetryAttempts = 3;
        });
        builder.Services.AddMediatorAzureServiceBus(o =>
        {
            o.ConnectionString = connection;
            o.EntityPath = queue;
        });
        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            await host.Services.GetRequiredService<IMediator>().Publish(new Ping { Text = Guid.NewGuid().ToString() });

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (recorder.Handled.Count < 3 && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            recorder.Handled.Should().BeEquivalentTo(new[] { nameof(PingHandlerA), nameof(FailsOnceHandler), nameof(FailsOnceHandler) },
                "the retry runs only the handler that failed");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class FailsOnceHandler(Recorder recorder) : INotificationHandler<Ping>
    {
        private static int s_calls;

        public Task Handle(Ping notification, CancellationToken cancellationToken)
        {
            recorder.Record(nameof(FailsOnceHandler));
            return Interlocked.Increment(ref s_calls) == 1 ? Task.FromException(new InvalidOperationException("first attempt fails")) : Task.CompletedTask;
        }
    }
}
