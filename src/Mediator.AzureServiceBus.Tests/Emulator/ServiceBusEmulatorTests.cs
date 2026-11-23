using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Mediator.AzureServiceBus.Tests.Emulator;

/// <summary>
/// End to end against the Azure Service Bus emulator: real hosts publish through the transport, the hosted processor
/// receives, and the broker does the scheduling, dead-lettering and topic filtering. Each test uses its own entity.
/// </summary>
[Collection(ServiceBusEmulatorCollection.Name)]
public sealed class ServiceBusEmulatorTests(ServiceBusEmulatorFixture emulator, ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // After the expected work is seen: how long to keep watching for work that must not happen (a handler running again).
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    [SkippableFact]
    public async Task Publish_ShouldRunEveryHandlerOnce_AndLeaveTheQueueEmpty()
    {
        emulator.RequireEmulator();
        var script = new HandlerScript();
        using var host = await StartHostAsync("handle", script);

        await host.Services.GetRequiredService<IMediator>().Publish(new EmulatorPing { Text = "hello" });

        await WaitUntilAsync(() => script.Calls(nameof(EmulatorHandlerA)) == 1 && script.Calls(nameof(EmulatorHandlerB)) == 1);
        await Task.Delay(Settle);
        script.Calls(nameof(EmulatorHandlerA)).Should().Be(1);
        script.Calls(nameof(EmulatorHandlerB)).Should().Be(1);
        script.Texts.Should().AllBe("hello", "the payload travels through the broker as the notification");
        (await PeekAsync("handle")).Should().BeNull("the message is completed");
        (await PeekAsync("handle", SubQueue.DeadLetter)).Should().BeNull();
        await host.StopAsync();
    }

    [SkippableFact]
    public async Task AFailedHandler_ShouldBeRetriedAlone_AfterTheBackoff()
    {
        emulator.RequireEmulator();
        var script = new HandlerScript { FailB = call => call == 1 };
        using var host = await StartHostAsync("retry", script, o => o.InitialRetryDelay = TimeSpan.FromSeconds(3));

        await host.Services.GetRequiredService<IMediator>().Publish(new EmulatorPing());

        await WaitUntilAsync(() => script.Calls(nameof(EmulatorHandlerB)) == 2);
        await Task.Delay(Settle);
        script.Calls(nameof(EmulatorHandlerA)).Should().Be(1, "handler A succeeded and must not run again");
        script.Calls(nameof(EmulatorHandlerB)).Should().Be(2);
        var (first, retry) = (script.Times(nameof(EmulatorHandlerB))[0], script.Times(nameof(EmulatorHandlerB))[1]);
        (retry - first).Should().BeGreaterThan(TimeSpan.FromSeconds(2.5), "the broker holds the scheduled retry until the backoff has passed");
        (await PeekAsync("retry")).Should().BeNull();
        (await PeekAsync("retry", SubQueue.DeadLetter)).Should().BeNull();
        await host.StopAsync();
    }

    [SkippableFact]
    public async Task AHandlerThatKeepsFailing_ShouldEndInTheDeadLetterQueue_Named()
    {
        emulator.RequireEmulator();
        var script = new HandlerScript { FailB = _ => true };
        using var host = await StartHostAsync("dead-letter", script, o =>
        {
            o.MaxRetryAttempts = 1;
            o.InitialRetryDelay = TimeSpan.Zero;
        });

        await host.Services.GetRequiredService<IMediator>().Publish(new EmulatorPing());

        var deadLettered = await ReceiveAsync("dead-letter", SubQueue.DeadLetter);
        deadLettered.Should().NotBeNull("a handler that is out of attempts is dead-lettered, not dropped");
        deadLettered!.DeadLetterReason.Should().Be(ServiceBusNotificationHandler.MaxRetriesReason);
        deadLettered.DeadLetterErrorDescription.Should().Contain("B failed");
        var handlerB = $"{typeof(EmulatorHandlerB).FullName}, {typeof(EmulatorHandlerB).Assembly.GetName().Name}";
        deadLettered.ApplicationProperties[ServiceBusMessageMapper.FailedHandlersProperty].Should().Be(handlerB);
        deadLettered.ApplicationProperties[ServiceBusMessageMapper.TargetHandlerProperty].Should().Be(handlerB, "the dead-lettered message is the retry of handler B alone");
        script.Calls(nameof(EmulatorHandlerA)).Should().Be(1);
        script.Calls(nameof(EmulatorHandlerB)).Should().Be(2, "the first attempt and one retry");
        await host.StopAsync();
    }

    [SkippableFact]
    public async Task AMessageNotSentByTheMediator_ShouldBeDeadLetteredAsInvalid_WithoutRunningHandlers()
    {
        emulator.RequireEmulator();
        var script = new HandlerScript();
        using var host = await StartHostAsync("poison", script);
        await using (var client = new ServiceBusClient(emulator.ConnectionString))
        {
            await client.CreateSender("poison").SendMessageAsync(new ServiceBusMessage("{\"some\":\"other app\"}"));
        }

        var deadLettered = await ReceiveAsync("poison", SubQueue.DeadLetter);

        deadLettered.Should().NotBeNull();
        deadLettered!.DeadLetterReason.Should().Be(ServiceBusNotificationHandler.InvalidMessageReason);
        script.Calls(nameof(EmulatorHandlerA)).Should().Be(0);
        await host.StopAsync();
    }

    [SkippableFact]
    public async Task ANotificationTypeThisAppDoesNotKnow_ShouldBeDeadLetteredAsPoison()
    {
        emulator.RequireEmulator();
        var script = new HandlerScript();
        using var host = await StartHostAsync("unknown-type", script);
        var envelope = new Mediator.Transport.NotificationEnvelope(Guid.NewGuid().ToString("N"), "Other.App.SomethingHappened, Other.App", "{}");
        await using (var client = new ServiceBusClient(emulator.ConnectionString))
        {
            await client.CreateSender("unknown-type").SendMessageAsync(ServiceBusMessageMapper.ToMessage(envelope));
        }

        var deadLettered = await ReceiveAsync("unknown-type", SubQueue.DeadLetter);

        deadLettered.Should().NotBeNull();
        deadLettered!.DeadLetterReason.Should().Be(ServiceBusNotificationHandler.PoisonReason, "retrying can never succeed, so it is dead-lettered at once");
        deadLettered.DeadLetterErrorDescription.Should().Contain("Other.App.SomethingHappened");
        await host.StopAsync();
    }

    [SkippableFact]
    public async Task OnATopic_EachSubscriptionHandlesTheNotificationOnce_AndKeepsItsRetriesToItself()
    {
        emulator.RequireEmulator();
        var billing = new HandlerScript { FailB = call => call == 1 };
        var shipping = new HandlerScript();
        using var billingHost = await StartHostAsync("events", billing, o => o.InitialRetryDelay = TimeSpan.Zero, subscription: "billing");
        using var shippingHost = await StartHostAsync("events", shipping, subscription: "shipping");
        using var publisher = await StartHostAsync("events", new HandlerScript(), publishOnly: true);

        await publisher.Services.GetRequiredService<IMediator>().Publish(new EmulatorPing());

        await WaitUntilAsync(() => billing.Calls(nameof(EmulatorHandlerB)) == 2 && shipping.Calls(nameof(EmulatorHandlerB)) == 1);
        await Task.Delay(Settle);
        billing.Calls(nameof(EmulatorHandlerA)).Should().Be(1);
        billing.Calls(nameof(EmulatorHandlerB)).Should().Be(2, "billing retries the handler that failed there");
        shipping.Calls(nameof(EmulatorHandlerA)).Should().Be(1);
        shipping.Calls(nameof(EmulatorHandlerB)).Should().Be(1, "billing's retry must not run shipping's handlers again");
        (await PeekAsync("events", subscription: "shipping")).Should().BeNull("shipping completes retries meant for billing");
        (await PeekAsync("events", SubQueue.DeadLetter, "billing")).Should().BeNull();
        await Task.WhenAll(billingHost.StopAsync(), shippingHost.StopAsync(), publisher.StopAsync());
    }

    private async Task<IHost> StartHostAsync(string entity, HandlerScript script, Action<MediatorOptions>? mediator = null,
        string? subscription = null, bool publishOnly = false)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new TestOutputLoggerProvider(output));
        builder.Services.AddSingleton(script);
        builder.Services.AddSingleton<INotificationSerializer>(new JsonNotificationSerializer());
        builder.Services.AddTransient<INotificationHandler<EmulatorPing>, EmulatorHandlerA>();
        builder.Services.AddTransient<INotificationHandler<EmulatorPing>, EmulatorHandlerB>();
        builder.Services.AddMediator(o =>
        {
            o.MaxRetryAttempts = 3;
            o.InitialRetryDelay = TimeSpan.Zero;
            mediator?.Invoke(o);
        });
        builder.Services.AddMediatorAzureServiceBus(o =>
        {
            o.ConnectionString = emulator.ConnectionString;
            o.EntityPath = entity;
            o.SubscriptionName = subscription;
            o.EnableProcessor = !publishOnly;
        });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private async Task<ServiceBusReceivedMessage?> PeekAsync(string entity, SubQueue subQueue = SubQueue.None, string? subscription = null)
    {
        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await using var receiver = CreateReceiver(client, entity, subscription, subQueue);
        return await receiver.PeekMessageAsync();
    }

    private async Task<ServiceBusReceivedMessage?> ReceiveAsync(string entity, SubQueue subQueue, string? subscription = null)
    {
        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await using var receiver = CreateReceiver(client, entity, subscription, subQueue);
        var message = await receiver.ReceiveMessageAsync(Timeout);
        if (message != null) await receiver.CompleteMessageAsync(message);
        return message;
    }

    private static ServiceBusReceiver CreateReceiver(ServiceBusClient client, string entity, string? subscription, SubQueue subQueue)
    {
        var options = new ServiceBusReceiverOptions { SubQueue = subQueue };
        return subscription == null ? client.CreateReceiver(entity, options) : client.CreateReceiver(entity, subscription, options);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }
}

public class EmulatorPing : INotification
{
    public string Text { get; set; } = "";
}

/// <summary>Records each handler call and decides, per call number, whether handler B fails.</summary>
public sealed class HandlerScript
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> _calls = new();
    private readonly ConcurrentQueue<string> _texts = new();

    public Func<int, bool> FailB { get; init; } = _ => false;

    public IReadOnlyCollection<string> Texts => _texts;

    public int Calls(string handler) => _calls.TryGetValue(handler, out var calls) ? calls.Count : 0;

    public IReadOnlyList<DateTime> Times(string handler) => _calls.TryGetValue(handler, out var calls) ? calls.ToArray() : [];

    /// <returns>The 1-based number of this call to <paramref name="handler"/>.</returns>
    public int Record(string handler, string text)
    {
        _texts.Enqueue(text);
        var calls = _calls.GetOrAdd(handler, _ => new ConcurrentQueue<DateTime>());
        calls.Enqueue(DateTime.UtcNow);
        return calls.Count;
    }
}

public sealed class EmulatorHandlerA(HandlerScript script) : INotificationHandler<EmulatorPing>
{
    public Task Handle(EmulatorPing notification, CancellationToken cancellationToken)
    {
        script.Record(nameof(EmulatorHandlerA), notification.Text);
        return Task.CompletedTask;
    }
}

public sealed class EmulatorHandlerB(HandlerScript script) : INotificationHandler<EmulatorPing>
{
    public Task Handle(EmulatorPing notification, CancellationToken cancellationToken)
    {
        var call = script.Record(nameof(EmulatorHandlerB), notification.Text);
        return script.FailB(call) ? Task.FromException(new InvalidOperationException("B failed")) : Task.CompletedTask;
    }
}
