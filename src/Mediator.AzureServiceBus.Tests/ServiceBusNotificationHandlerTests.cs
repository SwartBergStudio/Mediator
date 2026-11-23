using Azure.Messaging.ServiceBus;
using Mediator.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mediator.AzureServiceBus.Tests;

/// <summary>
/// How a received message is settled for each outcome of its handlers. Uses the real mediator dispatcher and handlers;
/// only the broker (sender and settlement) is recorded.
/// </summary>
public sealed class ServiceBusNotificationHandlerTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly Recorder _recorder = new();
    private readonly RecordingSender _sender = new();
    private readonly RecordingSettler _settler = new();
    private readonly MediatorOptions _mediatorOptions = new() { MaxRetryAttempts = 3, InitialRetryDelay = TimeSpan.FromMinutes(1), RetryDelayMultiplier = 2 };
    private readonly AzureServiceBusNotificationOptions _options = new() { EntityPath = "notifications" };

    public ServiceBusNotificationHandlerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_recorder);
        services.AddTransient<INotificationHandler<Ping>, PingHandlerA>();
        services.AddTransient<INotificationHandler<Ping>, PingHandlerB>();
        services.AddMediator();
        services.AddSingleton<INotificationSerializer>(new JsonNotificationSerializer());
        _provider = services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    private ServiceBusNotificationHandler Handler(INotificationDispatcher? dispatcher = null) => new(
        dispatcher ?? _provider.GetRequiredService<INotificationDispatcher>(),
        new AzureServiceBusNotificationTransport(_sender),
        Options.Create(_mediatorOptions),
        Options.Create(_options),
        NullLogger<ServiceBusNotificationHandler>.Instance);

    private NotificationEnvelope Envelope(Ping ping, int attempt = 0, string? target = null) =>
        new(Guid.NewGuid().ToString("N"), typeof(Ping).AssemblyQualifiedName!,
            _provider.GetRequiredService<INotificationSerializer>().Serialize(ping, typeof(Ping)))
        {
            AttemptCount = attempt,
            TargetHandlerType = target,
        };

    private static string Key<T>() => $"{typeof(T).FullName}, {typeof(T).Assembly.GetName().Name}";

    [Fact]
    public async Task AllHandlersSucceed_ShouldComplete()
    {
        await Handler().HandleAsync(Messages.For(Envelope(new Ping())), _settler, default);

        _settler.Calls.Should().Equal("Complete");
        _sender.Sent.Should().BeEmpty();
        _recorder.Handled.Should().BeEquivalentTo(nameof(PingHandlerA), nameof(PingHandlerB));
    }

    [Fact]
    public async Task OneOfTwoHandlersFails_ShouldScheduleARetryForOnlyThatHandler_ThenComplete()
    {
        var before = DateTimeOffset.UtcNow;
        var original = Envelope(new Ping { FailB = true });

        await Handler().HandleAsync(Messages.For(original), _settler, default);

        _settler.Calls.Should().Equal("Complete");
        var retry = _sender.Sent.Should().ContainSingle().Subject;
        retry.ApplicationProperties[ServiceBusMessageMapper.TargetHandlerProperty].Should().Be(Key<PingHandlerB>());
        retry.ApplicationProperties[ServiceBusMessageMapper.AttemptProperty].Should().Be(1);
        retry.MessageId.Should().NotBe(original.MessageId, "duplicate detection must not drop the retry");
        retry.ScheduledEnqueueTime.Should().BeCloseTo(before + _mediatorOptions.InitialRetryDelay, TimeSpan.FromSeconds(10));

        // Delivering the retry runs only the handler that failed.
        _recorder.Handled.Should().HaveCount(2);
        var settler = new RecordingSettler();
        await Handler().HandleAsync(Messages.Received(retry), settler, default);
        _recorder.Handled.Skip(2).Should().Equal(nameof(PingHandlerB));
    }

    [Fact]
    public async Task EveryHandlerFails_ShouldScheduleOneRetryOfTheWholeMessage()
    {
        await Handler().HandleAsync(Messages.For(Envelope(new Ping { FailA = true, FailB = true })), _settler, default);

        _settler.Calls.Should().Equal("Complete");
        var retry = _sender.Sent.Should().ContainSingle().Subject;
        retry.ApplicationProperties.Should().NotContainKey(ServiceBusMessageMapper.TargetHandlerProperty);
        retry.ApplicationProperties[ServiceBusMessageMapper.AttemptProperty].Should().Be(1);
    }

    [Fact]
    public void RetriesBackOffExponentially_UpToTheMaximumDelay()
    {
        _options.MaxRetryDelay = TimeSpan.FromMinutes(3);
        var handler = Handler();

        handler.RetryDelay(0).Should().Be(TimeSpan.FromMinutes(1));
        handler.RetryDelay(1).Should().Be(TimeSpan.FromMinutes(2));
        handler.RetryDelay(2).Should().Be(TimeSpan.FromMinutes(3));
        handler.RetryDelay(int.MaxValue).Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task OutOfAttempts_ShouldDeadLetter_NamingTheFailedHandler()
    {
        await Handler().HandleAsync(Messages.For(Envelope(new Ping { FailB = true }, attempt: 3)), _settler, default);

        _settler.Calls.Should().Equal("DeadLetter");
        _settler.DeadLetterReason.Should().Be(ServiceBusNotificationHandler.MaxRetriesReason);
        _settler.DeadLetterDescription.Should().Contain("B failed");
        _settler.DeadLetterProperties![ServiceBusMessageMapper.FailedHandlersProperty].Should().Be(Key<PingHandlerB>());
        _sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownNotificationType_ShouldDeadLetterAsPoison()
    {
        var envelope = new NotificationEnvelope("m", "No.Such.Type, NoSuchAssembly", "{}");

        await Handler().HandleAsync(Messages.For(envelope), _settler, default);

        _settler.Calls.Should().Equal("DeadLetter");
        _settler.DeadLetterReason.Should().Be(ServiceBusNotificationHandler.PoisonReason);
        _recorder.Handled.Should().BeEmpty();
    }

    [Fact]
    public async Task AMessageNotSentByTheMediator_ShouldDeadLetterAsInvalid()
    {
        var foreign = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("{}"), messageId: "x");

        await Handler().HandleAsync(foreign, _settler, default);

        _settler.Calls.Should().Equal("DeadLetter");
        _settler.DeadLetterReason.Should().Be(ServiceBusNotificationHandler.InvalidMessageReason);
    }

    [Fact]
    public async Task RetryThatCannotBeSent_ShouldAbandonSoTheBrokerRedelivers_NotComplete()
    {
        _sender.Failure = new ServiceBusException("namespace unavailable", ServiceBusFailureReason.ServiceCommunicationProblem);

        await Handler().HandleAsync(Messages.For(Envelope(new Ping { FailB = true })), _settler, default);

        _settler.Calls.Should().Equal("Abandon");
    }

    [Fact]
    public async Task DispatcherThatThrows_ShouldAbandon()
    {
        await Handler(new ThrowingDispatcher()).HandleAsync(Messages.For(Envelope(new Ping())), _settler, default);

        _settler.Calls.Should().Equal("Abandon");
    }

    [Fact]
    public async Task OnATopic_RetriesShouldBeAddressedToThisSubscription()
    {
        _options.SubscriptionName = "billing";

        await Handler().HandleAsync(Messages.For(Envelope(new Ping { FailB = true })), _settler, default);

        _sender.Sent.Single().ApplicationProperties[ServiceBusMessageMapper.SubscriptionProperty].Should().Be("billing");
    }

    [Fact]
    public async Task OnATopic_ARetryForAnotherSubscription_ShouldBeCompletedWithoutRunningHandlers()
    {
        _options.SubscriptionName = "billing";

        await Handler().HandleAsync(Messages.For(Envelope(new Ping()), retryForSubscription: "shipping"), _settler, default);

        _settler.Calls.Should().Equal("Complete");
        _recorder.Handled.Should().BeEmpty("the shipping subscription handles its own retries");
    }

    private sealed class ThrowingDispatcher : INotificationDispatcher
    {
        public Task<NotificationDispatchResult> DispatchAsync(NotificationEnvelope envelope, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }
}
