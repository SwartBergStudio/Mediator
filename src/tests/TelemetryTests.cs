using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// Traces and metrics, collected the way OpenTelemetry does (ActivityListener / MeterListener). Listeners are
    /// process-wide, so each test only looks at its own message types.
    /// </summary>
    [Collection("Mediator Integration Tests")]
    public sealed class TelemetryTests : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _activities = new();
        private readonly ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = new();
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;

        public TelemetryTests()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == MediatorDiagnostics.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _activities.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == MediatorDiagnostics.MeterName) listener.EnableMeasurementEvents(instrument);
                },
            };
            _meterListener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
            _meterListener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
            _meterListener.SetMeasurementEventCallback<int>((i, v, tags, _) => Record(i, v, tags));
            _meterListener.Start();
        }

        [Fact]
        public async Task Send_ShouldCreateSpanAroundPipeline_AndRecordDuration()
        {
            using var provider = Build();
            var request = new TracedPing();

            var result = await provider.GetRequiredService<IMediator>().Send(request);

            result.Should().Be("pong");
            var span = Single("send TracedPing");
            span.GetTagItem("mediator.message.type").Should().Be(typeof(TracedPing).FullName);
            span.Status.Should().Be(ActivityStatusCode.Unset);
            request.ActivityInsideHandler.Should().Be(span.Id, "the handler runs inside the mediator span, so its own spans become children");
            Measurements("mediator.request.duration", typeof(TracedPing)).Should().ContainSingle();
        }

        [Fact]
        public async Task FailingCommand_ShouldMarkSpanAsError_AndTagTheMetric()
        {
            using var provider = Build();

            await provider.GetRequiredService<IMediator>().Invoking(m => m.Send(new TracedFailingCommand()))
                .Should().ThrowAsync<InvalidOperationException>();

            var span = Single("send TracedFailingCommand");
            span.Status.Should().Be(ActivityStatusCode.Error);
            span.GetTagItem("error.type").Should().Be(typeof(InvalidOperationException).FullName);
            span.Events.Should().Contain(e => e.Name == "exception");
            Measurements("mediator.request.duration", typeof(TracedFailingCommand))
                .Should().ContainSingle().Which.Tags["error.type"].Should().Be(typeof(InvalidOperationException).FullName);
        }

        [Fact]
        public async Task Stream_ShouldCreateSpanCoveringTheEnumeration()
        {
            using var provider = Build();

            var items = new List<int>();
            await foreach (var item in provider.GetRequiredService<IMediator>().CreateStream(new TracedCount())) items.Add(item);

            items.Should().Equal(1, 2, 3);
            Single("stream TracedCount");
            Measurements("mediator.request.duration", typeof(TracedCount)).Should().ContainSingle();
        }

        [Fact]
        public async Task BackgroundHandlers_ShouldBeChildrenOfThePublishSpan_WithPerHandlerMetrics()
        {
            using var provider = Build();

            await provider.GetRequiredService<IMediator>().Publish(new TracedEvent());
            await Eventually.WaitUntilAsync(() => _activities.Count(a => a.DisplayName == "handle TracedEvent") == 2);

            var publish = Single("publish TracedEvent");
            publish.Kind.Should().Be(ActivityKind.Producer);
            publish.GetTagItem("mediator.publish.mode").Should().Be("background");

            var handlerSpans = _activities.Where(a => a.DisplayName == "handle TracedEvent").ToList();
            handlerSpans.Should().HaveCount(2).And.OnlyContain(a => a.ParentSpanId == publish.SpanId && a.TraceId == publish.TraceId);
            handlerSpans.Should().ContainSingle(a => a.Status == ActivityStatusCode.Error, "TracedEventFailingHandler throws");

            Measurements("mediator.notification.published", typeof(TracedEvent)).Should().ContainSingle()
                .Which.Tags["mediator.publish.mode"].Should().Be("background");
            Measurements("mediator.notification.handler.duration", typeof(TracedEvent)).Should().HaveCount(2)
                .And.ContainSingle(m => m.Tags.ContainsKey("error.type"));
        }

        [Fact]
        public async Task PublishAndWait_ShouldTraceEachHandlerInsideTheCallersSpan()
        {
            using var provider = Build();
            using var scope = provider.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IMediator>().PublishAndWait(new TracedAwaitedEvent());

            var publish = Single("publish TracedAwaitedEvent");
            publish.GetTagItem("mediator.publish.mode").Should().Be("awaited");
            Single("handle TracedAwaitedEvent").ParentSpanId.Should().Be(publish.SpanId);
        }

        [Fact]
        public void QueueSizeGauge_ShouldBeObservable()
        {
            using var provider = Build();
            provider.GetRequiredService<INotificationPublisher>(); // starts the background workers

            _meterListener.RecordObservableInstruments();

            _measurements.Should().Contain(m => m.Instrument == "mediator.notification.queue.size" && m.Value >= 0);
        }

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }

        private Activity Single(string displayName)
        {
            var matches = _activities.Where(a => a.DisplayName == displayName).ToList();
            matches.Should().ContainSingle($"exactly one '{displayName}' span is expected");
            return matches[0];
        }

        private List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements(string instrument, Type messageType)
            => _measurements.Where(m => m.Instrument == instrument && Equals(m.Tags.GetValueOrDefault("mediator.message.type"), messageType.FullName)).ToList();

        private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags) where T : struct
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags) copy[tag.Key] = tag.Value;
            _measurements.Enqueue((instrument.Name, Convert.ToDouble(value), copy));
        }

        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddTransient<IRequestHandler<TracedPing, string>, TracedPingHandler>();
            services.AddTransient<IRequestHandler<TracedFailingCommand>, TracedFailingCommandHandler>();
            services.AddTransient<IStreamRequestHandler<TracedCount, int>, TracedCountHandler>();
            services.AddTransient<INotificationHandler<TracedEvent>, TracedEventHandler>();
            services.AddTransient<INotificationHandler<TracedEvent>, TracedEventFailingHandler>();
            services.AddTransient<INotificationHandler<TracedAwaitedEvent>, TracedAwaitedEventHandler>();
            services.AddMediator(options => options.NotificationWorkerCount = 1);
            return services.BuildServiceProvider();
        }

        public sealed class TracedPing : IRequest<string>
        {
            public string? ActivityInsideHandler { get; set; }
        }

        public sealed class TracedPingHandler : IRequestHandler<TracedPing, string>
        {
            public async Task<string> Handle(TracedPing request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                request.ActivityInsideHandler = Activity.Current?.Id;
                return "pong";
            }
        }

        public sealed class TracedFailingCommand : IRequest;

        public sealed class TracedFailingCommandHandler : IRequestHandler<TracedFailingCommand>
        {
            public Task Handle(TracedFailingCommand request, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
        }

        public sealed class TracedCount : IStreamRequest<int>;

        public sealed class TracedCountHandler : IStreamRequestHandler<TracedCount, int>
        {
            public async IAsyncEnumerable<int> Handle(TracedCount request, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                for (var i = 1; i <= 3; i++)
                {
                    await Task.Yield();
                    yield return i;
                }
            }
        }

        public sealed class TracedEvent : INotification;

        public sealed class TracedEventHandler : INotificationHandler<TracedEvent>
        {
            public Task Handle(TracedEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        public sealed class TracedEventFailingHandler : INotificationHandler<TracedEvent>
        {
            public Task Handle(TracedEvent notification, CancellationToken cancellationToken) => throw new InvalidOperationException("handler failed");
        }

        public sealed class TracedAwaitedEvent : INotification;

        public sealed class TracedAwaitedEventHandler : INotificationHandler<TracedAwaitedEvent>
        {
            public Task Handle(TracedAwaitedEvent notification, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
