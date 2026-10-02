using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    [Collection("Mediator Integration Tests")]
    public class RegressionTests : IDisposable
    {
        private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), "mediator-regression-tests", Guid.NewGuid().ToString());

        [Fact]
        public async Task Publish_WithPersistence_ShouldHandleNotificationExactlyOnce()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            var tracker = new TestNotificationTracker();
            services.AddSingleton<ITestNotificationTracker>(tracker);
            services.AddTransient<INotificationHandler<TestNotification>, TestNotificationHandler>();
            services.AddMediator(options =>
            {
                options.EnablePersistence = true;
                options.ProcessingInterval = TimeSpan.FromMilliseconds(50);
            });
            services.AddSingleton<INotificationPersistence>(_ => new FileNotificationPersistence(_testDirectory));

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            await mediator.Publish(new TestNotification { Message = "once" });
            await Eventually.WaitUntilAsync(() => tracker.GetHandleCount("TestNotificationHandler") > 0);

            // Several recovery passes (every 50 ms) run in this window; previously each persisted notification was
            // handled again. This fixed wait is intentional: it gives a duplicate the chance to appear.
            await Task.Delay(600);

            tracker.GetHandleCount("TestNotificationHandler").Should().Be(1);
            Directory.GetFiles(_testDirectory, "*.json").Should().BeEmpty("completed notifications are removed from persistence");
        }

        [Fact]
        public async Task Recovery_StaleSnapshot_ShouldNotRequeueNotificationFinishedMeanwhile()
        {
            // Forces the race deterministically: the recovery loop takes its snapshot while the notification is still
            // stored, the worker then finishes it, and only afterwards does the recovery loop process its snapshot.
            var store = new SnapshotHoldingPersistence(new FileNotificationPersistence(_testDirectory));
            var handled = new HandledCounter();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton(handled);
            services.AddSingleton(store);
            services.AddTransient<INotificationHandler<SnapshotRaceEvent>, SnapshotRaceHandler>();
            services.AddSingleton<INotificationPersistence>(store);
            services.AddMediator(options =>
            {
                options.EnablePersistence = true;
                options.NotificationWorkerCount = 1;
                options.ProcessingInterval = TimeSpan.FromMilliseconds(20);
            });
            using var provider = services.BuildServiceProvider();

            await provider.GetRequiredService<IMediator>().Publish(new SnapshotRaceEvent());

            await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);           // let the worker finish its bookkeeping
            store.ReleaseSnapshot();         // the recovery loop now processes its stale snapshot
            await Task.Delay(300);

            handled.Count.Should().Be(1, "a notification finished after the recovery snapshot was taken must not run again");
        }

        [Fact]
        public void AddMediator_ScanningAssemblyWithOpenGenericHandler_ShouldNotThrowOnBuild()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMediator(typeof(OpenGenericStreamBehavior<,>).Assembly);

            var act = () => services.BuildServiceProvider().Dispose();

            act.Should().NotThrow();
            services.Should().NotContain(d => d.ImplementationType == typeof(OpenGenericStreamBehavior<,>));
        }

        [Fact]
        public async Task FileNotificationPersistence_RetryAfterInPast_ShouldBeReturnedAsPending()
        {
            using var persistence = new FileNotificationPersistence(_testDirectory);
            var workItem = new NotificationWorkItem(null, typeof(TestNotification), DateTime.UtcNow, "{\"message\":\"x\"}");
            var id = await persistence.PersistAsync(workItem);

            await persistence.FailAsync(id, new Exception("fail"), DateTime.UtcNow.AddSeconds(-1));

            var pending = await persistence.GetPendingAsync();
            pending.Should().ContainSingle(p => p.Id == id && p.AttemptCount == 1);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_testDirectory)) Directory.Delete(_testDirectory, true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Open generic behavior living in the scanned test assembly. Assembly scanning must skip it.
    /// </summary>
    public class OpenGenericStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
        where TRequest : IStreamRequest<TResponse>
    {
        public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
    }

    public sealed class SnapshotRaceEvent : INotification;

    public sealed class SnapshotRaceHandler(HandledCounter handled, SnapshotHoldingPersistence store) : INotificationHandler<SnapshotRaceEvent>
    {
        public async Task Handle(SnapshotRaceEvent notification, CancellationToken cancellationToken)
        {
            // Stay in progress until the recovery loop has taken a snapshot that includes this notification.
            await store.SnapshotTaken.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            handled.Increment();
        }
    }

    /// <summary>
    /// Wraps a store: the first non-empty snapshot is held back until released, and completion is observable.
    /// </summary>
    public sealed class SnapshotHoldingPersistence(INotificationPersistence inner) : INotificationPersistence
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public TaskCompletionSource SnapshotTaken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseSnapshot() => _release.TrySetResult();

        public async Task<IEnumerable<PersistedNotificationWorkItem>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
        {
            var snapshot = (await inner.GetPendingAsync(batchSize, cancellationToken)).ToList();
            if (snapshot.Count > 0 && Interlocked.Exchange(ref _held, 1) == 0)
            {
                SnapshotTaken.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return snapshot;
        }

        public async Task CompleteAsync(string id, CancellationToken cancellationToken = default)
        {
            await inner.CompleteAsync(id, cancellationToken);
            Completed.TrySetResult();
        }

        public Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default) => inner.PersistAsync(workItem, cancellationToken);
        public Task FailAsync(string id, Exception exception, DateTime? retryAfter = null, CancellationToken cancellationToken = default) => inner.FailAsync(id, exception, retryAfter, cancellationToken);
        public Task CleanupAsync(DateTime olderThan, CancellationToken cancellationToken = default) => inner.CleanupAsync(olderThan, cancellationToken);
        public void Dispose() => inner.Dispose();
    }
}
