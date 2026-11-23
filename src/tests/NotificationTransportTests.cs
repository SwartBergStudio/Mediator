using System.Collections.Concurrent;
using System.Diagnostics;
using Mediator.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// The broker seam: <see cref="INotificationTransport"/> replaces the in-process queue on the publishing side, and
    /// <see cref="INotificationDispatcher"/> runs the handlers on the consuming side.
    /// </summary>
    [Collection("Mediator Integration Tests")]
    public sealed class NotificationTransportTests : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _activities = new();
        private readonly ActivityListener _listener;

        public NotificationTransportTests()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == MediatorDiagnostics.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _activities.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Publish_WithATransport_ShouldSendOneEnvelope_AndNotRunHandlersInProcess(bool transportFirst)
        {
            var (provider, transport, recorder) = Build(transportFirst: transportFirst);
            await using var _ = provider;

            await provider.GetRequiredService<IMediator>().Publish(new TransportPing { Text = "hello" });

            var envelope = transport.Sent.Should().ContainSingle().Subject;
            envelope.NotificationType.Should().Be(typeof(TransportPing).AssemblyQualifiedName);
            envelope.Payload.Should().Contain("hello");
            envelope.MessageId.Should().NotBeNullOrEmpty();
            envelope.AttemptCount.Should().Be(0);
            envelope.TargetHandlerType.Should().BeNull();
            provider.GetRequiredService<INotificationPublisher>().GetType().Name.Should().Be("TransportNotificationPublisher",
                "the in-process background queue must not be created at all when a transport carries the notifications");
            recorder.Handled.Should().BeEmpty("handlers run on the consuming side, from the broker");
        }

        [Fact]
        public async Task Publish_WhenTheTransportFails_ShouldFault()
        {
            var (provider, transport, _) = Build();
            await using var __ = provider;
            transport.Failure = new InvalidOperationException("broker unavailable");

            var publish = () => provider.GetRequiredService<IMediator>().Publish(new TransportPing());

            await publish.Should().ThrowAsync<InvalidOperationException>().WithMessage("broker unavailable");
        }

        [Fact]
        public async Task Dispatch_ShouldRunEveryHandler_EachInItsOwnScope()
        {
            var (provider, transport, recorder) = Build();
            await using var _ = provider;
            await provider.GetRequiredService<IMediator>().Publish(new TransportPing { Text = "x" });

            var result = await provider.GetRequiredService<INotificationDispatcher>().DispatchAsync(transport.Sent.Single());

            result.Succeeded.Should().BeTrue();
            result.IsPoison.Should().BeFalse();
            result.HandlerCount.Should().Be(2);
            recorder.Handled.Select(h => h.Handler).Should().BeEquivalentTo(nameof(TransportPingHandlerA), nameof(TransportPingHandlerB));
            recorder.Handled.Select(h => h.Text).Should().AllBe("x", "the payload is deserialized back into the notification");
            recorder.Handled.Select(h => h.Scope).Distinct().Should().HaveCount(2, "each handler gets a DI scope of its own");
        }

        [Fact]
        public async Task Dispatch_WhenOneOfTwoHandlersFails_ShouldReportOnlyIt_AndARetryTargetingItShouldRunOnlyIt()
        {
            var (provider, transport, recorder) = Build();
            await using var _ = provider;
            await provider.GetRequiredService<IMediator>().Publish(new TransportPing { FailB = true });
            var dispatcher = provider.GetRequiredService<INotificationDispatcher>();

            var result = await dispatcher.DispatchAsync(transport.Sent.Single());

            result.Succeeded.Should().BeFalse();
            result.Error.Should().BeNull("the handlers ran; one of them failed");
            result.HandlerCount.Should().Be(2);
            var failure = result.FailedHandlers.Should().ContainSingle().Subject;
            failure.HandlerType.Should().Be($"{typeof(TransportPingHandlerB).FullName}, {typeof(TransportPingHandlerB).Assembly.GetName().Name}");
            failure.Error.Should().BeOfType<InvalidOperationException>();

            recorder.Clear();
            var retry = transport.Sent.Single().ForRetry(failure.HandlerType, notBefore: null);
            var retryResult = await dispatcher.DispatchAsync(retry);

            retry.AttemptCount.Should().Be(1);
            retry.MessageId.Should().NotBe(transport.Sent.Single().MessageId);
            retryResult.FailedHandlers.Should().ContainSingle();
            recorder.Handled.Select(h => h.Handler).Should().Equal(new[] { nameof(TransportPingHandlerB) }, "handler A already succeeded and must not run again");
        }

        [Fact]
        public async Task Dispatch_UnknownNotificationType_ShouldBePoison()
        {
            var (provider, _, recorder) = Build();
            await using var __ = provider;

            var result = await provider.GetRequiredService<INotificationDispatcher>()
                .DispatchAsync(new NotificationEnvelope("m1", "No.Such.Notification, NoSuchAssembly", "{}"));

            result.IsPoison.Should().BeTrue();
            result.Succeeded.Should().BeFalse();
            result.Error!.Message.Should().Contain("No.Such.Notification");
            recorder.Handled.Should().BeEmpty();
        }

        [Fact]
        public async Task Dispatch_PayloadThatCannotBeDeserialized_ShouldBePoison()
        {
            var (provider, _, recorder) = Build();
            await using var __ = provider;

            var result = await provider.GetRequiredService<INotificationDispatcher>()
                .DispatchAsync(new NotificationEnvelope("m1", typeof(TransportPing).AssemblyQualifiedName!, "not json"));

            result.IsPoison.Should().BeTrue();
            recorder.Handled.Should().BeEmpty();
        }

        [Fact]
        public async Task HandlerSpans_ShouldJoinThePublishersTrace()
        {
            var (provider, transport, _) = Build();
            await using var __ = provider;

            await provider.GetRequiredService<IMediator>().Publish(new TransportPing());
            var publish = _activities.Single(a => a.DisplayName == $"publish {nameof(TransportPing)}");
            transport.Sent.Single().TraceParent.Should().Be(publish.Id);

            await provider.GetRequiredService<INotificationDispatcher>().DispatchAsync(transport.Sent.Single());

            var handles = _activities.Where(a => a.DisplayName == $"handle {nameof(TransportPing)}").ToList();
            handles.Should().HaveCount(2);
            handles.Should().OnlyContain(h => h.TraceId == publish.TraceId && h.ParentSpanId == publish.SpanId);
        }

        [Fact]
        public async Task PersistenceWithATransport_ShouldBeIgnored_WithAWarning()
        {
            var log = new ConcurrentQueue<string>();
            var (provider, transport, _) = Build(enablePersistence: true, log: log);
            await using var _ = provider;

            await provider.GetRequiredService<IMediator>().Publish(new TransportPing());

            transport.Sent.Should().ContainSingle();
            log.Should().Contain(l => l.StartsWith("Warning") && l.Contains("EnablePersistence is ignored"));
        }

        private static (ServiceProvider Provider, RecordingTransport Transport, TransportRecorder Recorder) Build(
            bool transportFirst = false, bool enablePersistence = false, ConcurrentQueue<string>? log = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Warning);
                if (log != null) builder.AddProvider(new QueueLoggerProvider(log));
            });
            var transport = new RecordingTransport();
            var recorder = new TransportRecorder();
            services.AddSingleton(recorder);
            services.AddScoped<ScopeMarker>();
            services.AddTransient<INotificationHandler<TransportPing>, TransportPingHandlerA>();
            services.AddTransient<INotificationHandler<TransportPing>, TransportPingHandlerB>();

            if (transportFirst) AddTransport(services, transport);
            services.AddMediator(o =>
            {
                o.EnablePersistence = enablePersistence;
                o.NotificationWorkerCount = 1;
            });
            if (!transportFirst) AddTransport(services, transport);

            return (services.BuildServiceProvider(), transport, recorder);
        }

        private static void AddTransport(IServiceCollection services, RecordingTransport transport)
        {
            services.AddNotificationTransport<RecordingTransport>();
            // Hand the test the instance the mediator uses.
            services.AddSingleton<INotificationTransport>(transport);
        }

        private sealed class RecordingTransport : INotificationTransport
        {
            public ConcurrentQueue<NotificationEnvelope> Sent { get; } = new();
            public Exception? Failure { get; set; }

            public Task SendAsync(NotificationEnvelope envelope, CancellationToken cancellationToken = default)
            {
                if (Failure != null) return Task.FromException(Failure);
                Sent.Enqueue(envelope);
                return Task.CompletedTask;
            }
        }

        private sealed class QueueLoggerProvider(ConcurrentQueue<string> log) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new QueueLogger(log);
            public void Dispose() { }

            private sealed class QueueLogger(ConcurrentQueue<string> log) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                    => log.Enqueue($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }

    public class TransportPing : INotification
    {
        public string Text { get; set; } = "";
        public bool FailB { get; set; }
    }

    public sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    public sealed class TransportRecorder
    {
        private ConcurrentQueue<(string Handler, string Text, Guid Scope)> _handled = new();
        public IReadOnlyCollection<(string Handler, string Text, Guid Scope)> Handled => _handled;
        public void Record(string handler, string text, Guid scope) => _handled.Enqueue((handler, text, scope));
        public void Clear() => _handled = new();
    }

    public sealed class TransportPingHandlerA(TransportRecorder recorder, ScopeMarker scope) : INotificationHandler<TransportPing>
    {
        public Task Handle(TransportPing notification, CancellationToken cancellationToken)
        {
            recorder.Record(nameof(TransportPingHandlerA), notification.Text, scope.Id);
            return Task.CompletedTask;
        }
    }

    public sealed class TransportPingHandlerB(TransportRecorder recorder, ScopeMarker scope) : INotificationHandler<TransportPing>
    {
        public Task Handle(TransportPing notification, CancellationToken cancellationToken)
        {
            recorder.Record(nameof(TransportPingHandlerB), notification.Text, scope.Id);
            return notification.FailB ? Task.FromException(new InvalidOperationException("B failed")) : Task.CompletedTask;
        }
    }
}
