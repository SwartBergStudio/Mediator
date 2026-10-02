using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// With persistence enabled, only handlers that failed are retried; handlers that succeeded never run again.
    /// </summary>
    [Collection("Mediator Integration Tests")]
    public sealed class HandlerRetryTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mediator-retry-tests", Guid.NewGuid().ToString());
        private readonly ConcurrentQueue<string> _log = new();

        /// <summary>The mediator's log for this test, attached to assertion messages to diagnose timing-related failures.</summary>
        private string Log => string.Join(Environment.NewLine, _log);

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task FailingHandler_ShouldBeRetriedAlone_OtherHandlersRunOnce(bool atomicRetryStore)
        {
            var calls = new RetryProbe();
            calls.FailuresLeft["Flaky"] = 1;
            using var provider = Build(calls, maxRetryAttempts: 3, atomicRetryStore);

            await provider.GetRequiredService<IMediator>().Publish(new OrderPlaced());
            await Eventually.WaitUntilAsync(() => calls.Count("Flaky") == 2 && PersistedFiles().Length == 0);

            calls.Count("Stable").Should().Be(1, "it succeeded the first time and must not run again. Log:\n{0}", Log);
            calls.Count("Other").Should().Be(1);
            calls.Count("Flaky").Should().Be(2, "it failed once and succeeded on its retry");
            PersistedFiles().Should().BeEmpty();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task HandlerThatKeepsFailing_ShouldBeRetriedUpToMaxThenDropped(bool atomicRetryStore)
        {
            var calls = new RetryProbe();
            calls.FailuresLeft["Flaky"] = int.MaxValue;
            using var provider = Build(calls, maxRetryAttempts: 2, atomicRetryStore);

            await provider.GetRequiredService<IMediator>().Publish(new OrderPlaced());
            await Eventually.WaitUntilAsync(() => calls.Count("Flaky") == 3 && PersistedFiles().Length == 0);
            await Task.Delay(300); // no further retries may appear after giving up

            calls.Count("Flaky").Should().Be(3, "initial attempt plus MaxRetryAttempts (2) retries. Log:\n{0}", Log);
            calls.Count("Stable").Should().Be(1);
            calls.Count("Other").Should().Be(1);
            PersistedFiles().Should().BeEmpty("the notification is dropped after the last retry");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TwoFailingHandlers_ShouldEachBeRetriedIndependently(bool atomicRetryStore)
        {
            var calls = new RetryProbe();
            calls.FailuresLeft["Flaky"] = 1;
            calls.FailuresLeft["Other"] = 2;
            using var provider = Build(calls, maxRetryAttempts: 3, atomicRetryStore);

            await provider.GetRequiredService<IMediator>().Publish(new OrderPlaced());
            await Eventually.WaitUntilAsync(() => calls.Count("Flaky") == 2 && calls.Count("Other") == 3 && PersistedFiles().Length == 0);

            calls.Count("Stable").Should().Be(1, "Log:\n{0}", Log);
            calls.Count("Flaky").Should().Be(2, "Log:\n{0}", Log);
            calls.Count("Other").Should().Be(3, "Log:\n{0}", Log);
        }

        [Fact]
        public async Task TargetHandlerType_ShouldRoundTripThroughFilePersistence()
        {
            using var persistence = new FileNotificationPersistence(_directory);
            var item = new NotificationWorkItem(null, typeof(OrderPlaced), DateTime.UtcNow, "{}") { TargetHandlerType = "App.Handlers.SendEmail, App" };

            var id = await persistence.PersistAsync(item);
            var pending = await persistence.GetPendingAsync();

            pending.Should().ContainSingle(p => p.Id == id).Which.WorkItem.TargetHandlerType.Should().Be("App.Handlers.SendEmail, App");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
            catch (IOException) { }
        }

        private string[] PersistedFiles() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.json") : Array.Empty<string>();

        /// <param name="atomicRetryStore">
        /// True: the file store, which implements INotificationRetryPersistence. False: the same store exposed only as
        /// INotificationPersistence, like an older custom implementation, to cover the fallback path.
        /// </param>
        private ServiceProvider Build(RetryProbe calls, int maxRetryAttempts, bool atomicRetryStore)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(new QueueLoggerProvider(_log)).SetMinimumLevel(LogLevel.Debug));
            services.AddSingleton(calls);
            services.AddTransient<INotificationHandler<OrderPlaced>, StableHandler>();
            services.AddTransient<INotificationHandler<OrderPlaced>, FlakyHandler>();
            services.AddTransient<INotificationHandler<OrderPlaced>, OtherHandler>();
            services.AddSingleton<INotificationPersistence>(_ => atomicRetryStore
                ? new FileNotificationPersistence(_directory)
                : new BasicPersistence(new FileNotificationPersistence(_directory)));
            services.AddMediator(options =>
            {
                options.EnablePersistence = true;
                options.NotificationWorkerCount = 1;
                options.ProcessingInterval = TimeSpan.FromMilliseconds(50);
                options.MaxRetryAttempts = maxRetryAttempts;
                options.InitialRetryDelay = TimeSpan.FromMilliseconds(20);
                options.RetryDelayMultiplier = 1.0;
            });
            return services.BuildServiceProvider();
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
                    => log.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {logLevel}: {formatter(state, null)}");
            }
        }

        /// <summary>Exposes a store through INotificationPersistence only (no INotificationRetryPersistence).</summary>
        private sealed class BasicPersistence(INotificationPersistence inner) : INotificationPersistence
        {
            public Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default) => inner.PersistAsync(workItem, cancellationToken);
            public Task<IEnumerable<PersistedNotificationWorkItem>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default) => inner.GetPendingAsync(batchSize, cancellationToken);
            public Task CompleteAsync(string id, CancellationToken cancellationToken = default) => inner.CompleteAsync(id, cancellationToken);
            public Task FailAsync(string id, Exception exception, DateTime? retryAfter = null, CancellationToken cancellationToken = default) => inner.FailAsync(id, exception, retryAfter, cancellationToken);
            public Task CleanupAsync(DateTime olderThan, CancellationToken cancellationToken = default) => inner.CleanupAsync(olderThan, cancellationToken);
            public void Dispose() => inner.Dispose();
        }

        public sealed class RetryProbe
        {
            private readonly ConcurrentDictionary<string, int> _calls = new();
            public ConcurrentDictionary<string, int> FailuresLeft { get; } = new();

            public int Count(string handler) => _calls.TryGetValue(handler, out var c) ? c : 0;

            public Task Run(string handler)
            {
                _calls.AddOrUpdate(handler, 1, (_, c) => c + 1);
                var failNow = false;
                FailuresLeft.AddOrUpdate(handler, 0, (_, left) =>
                {
                    failNow = left > 0;
                    return left > 0 && left != int.MaxValue ? left - 1 : left;
                });
                return failNow ? Task.FromException(new InvalidOperationException($"{handler} failed")) : Task.CompletedTask;
            }
        }

        public sealed class OrderPlaced : INotification
        {
            public string OrderId { get; set; } = "42";
        }

        public sealed class StableHandler(RetryProbe calls) : INotificationHandler<OrderPlaced>
        {
            public Task Handle(OrderPlaced notification, CancellationToken cancellationToken) => calls.Run("Stable");
        }

        public sealed class FlakyHandler(RetryProbe calls) : INotificationHandler<OrderPlaced>
        {
            public Task Handle(OrderPlaced notification, CancellationToken cancellationToken) => calls.Run("Flaky");
        }

        public sealed class OtherHandler(RetryProbe calls) : INotificationHandler<OrderPlaced>
        {
            public Task Handle(OrderPlaced notification, CancellationToken cancellationToken) => calls.Run("Other");
        }
    }
}
