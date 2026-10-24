using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// Background notifications (<see cref="IMediator.Publish{TNotification}"/>) must run, run once, and never take the
    /// publisher down: whatever a handler, its dependencies or the host's shutdown do.
    /// </summary>
    [Collection("Mediator Integration Tests")]
    public sealed class BackgroundNotificationRobustnessTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mediator-robustness-tests", Guid.NewGuid().ToString());
        private readonly ConcurrentQueue<string> _log = new();

        private string Log => string.Join(Environment.NewLine, _log);

        [Fact]
        public async Task HandlersOfOneNotification_ShouldEachGetTheirOwnScope()
        {
            // They run concurrently. Sharing one scope meant sharing scoped services that are not thread-safe, such as a
            // DbContext: "A second operation was started on this context instance".
            var probe = new Probe();
            using var provider = Build(probe, services =>
            {
                services.AddScoped<NotThreadSafeService>();
                services.AddTransient<INotificationHandler<Shared>, FirstSharedHandler>();
                services.AddTransient<INotificationHandler<Shared>, SecondSharedHandler>();
            });

            await provider.GetRequiredService<IMediator>().Publish(new Shared());
            await Eventually.WaitUntilAsync(() => probe.Completed + probe.Failed >= 2);

            probe.Failed.Should().Be(0, "Log:\n{0}", Log);
            probe.Completed.Should().Be(2);
        }

        [Fact]
        public async Task HandlerThatCannotBeResolved_ShouldBeRetriedWithBackoffThenDropped()
        {
            // Resolving the handlers threw before the result was settled, so the persisted item stayed ready and every
            // recovery pass tried again: no backoff, no attempt limit, forever.
            using var provider = Build(new Probe(), services =>
                services.AddTransient<INotificationHandler<Unresolvable>, UnresolvableHandler>(), persistence: true, maxRetryAttempts: 2);

            await provider.GetRequiredService<IMediator>().Publish(new Unresolvable());
            await Eventually.WaitUntilAsync(() => ResolveFailures() >= 3 && PersistedFiles().Length == 0);
            await Task.Delay(400); // eight recovery passes: no further attempts after giving up

            ResolveFailures().Should().Be(3, "the first attempt plus MaxRetryAttempts (2) retries. Log:\n{0}", Log);
            PersistedFiles().Should().BeEmpty("it is dropped after the last retry");
        }

        [Fact]
        public async Task HandlerUsingAnAsyncDisposableService_ShouldCompleteOnce()
        {
            // The scope was disposed synchronously, which throws for a service that is only IAsyncDisposable. That
            // happened after the handler succeeded but before the result was settled, so it ran again on every pass.
            var probe = new Probe();
            using var provider = Build(probe, services =>
            {
                services.AddScoped<AsyncOnlyDisposable>();
                services.AddTransient<INotificationHandler<UsesAsyncDisposable>, AsyncDisposableHandler>();
            }, persistence: true);

            await provider.GetRequiredService<IMediator>().Publish(new UsesAsyncDisposable());
            await Eventually.WaitUntilAsync(() => probe.Completed >= 1 && PersistedFiles().Length == 0);
            await Task.Delay(400); // eight recovery passes

            probe.Completed.Should().Be(1, "Log:\n{0}", Log);
            probe.Disposed.Should().Be(1, "the service is disposed asynchronously with its scope");
            PersistedFiles().Should().BeEmpty("Log:\n{0}", Log);
        }

        [Fact]
        public async Task Publish_NullNotification_ShouldReturnFaultedTask()
        {
            // Like Send and PublishAndWait. It used to be accepted, queued and fail later in the background.
            using var provider = Build(new Probe(), _ => { });

            await provider.GetRequiredService<IMediator>().Invoking(m => m.Publish<Shared>(null!))
                .Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task ZeroWorkers_ShouldStillProcessNotifications()
        {
            // With no workers nothing was ever handled, and Publish blocked forever once the channel was full.
            var probe = new Probe();
            using var provider = Build(probe, services =>
                services.AddTransient<INotificationHandler<Counted>, CountedHandler>(), workers: 0);

            await provider.GetRequiredService<IMediator>().Publish(new Counted());
            await Eventually.WaitUntilAsync(() => probe.Completed == 1, TimeSpan.FromSeconds(3));

            probe.Completed.Should().Be(1, "Log:\n{0}", Log);
        }

        [Fact]
        public async Task HostShutdown_ShouldFinishNotificationsAlreadyQueued()
        {
            // Stopping the app cancelled the workers at once, so queued notifications were dropped without a word.
            var probe = new Probe();
            using var host = BuildHost(probe, services => services.AddTransient<INotificationHandler<Slow>, SlowHandler>());
            await host.StartAsync();
            var mediator = host.Services.GetRequiredService<IMediator>();

            for (var i = 0; i < 10; i++)
                await mediator.Publish(new Slow());
            await host.StopAsync();

            probe.Completed.Should().Be(10, "graceful shutdown drains the queue. Log:\n{0}", Log);
            probe.Failed.Should().Be(0);
        }

        [Fact]
        public async Task ContainerDisposeWithoutHost_ShouldFinishTheRunningHandlerAndReportTheRest()
        {
            // Without a host, disposal starts after the provider can no longer create scopes: the handler already running
            // finishes instead of being cancelled, and what is still queued is reported instead of silently dropped.
            var probe = new Probe();
            var provider = Build(probe, services => services.AddTransient<INotificationHandler<Slow>, SlowHandler>());
            var mediator = provider.GetRequiredService<IMediator>();

            for (var i = 0; i < 10; i++)
                await mediator.Publish(new Slow());
            await Eventually.WaitUntilAsync(() => probe.Attempts >= 1);
            provider.Dispose();

            probe.Failed.Should().Be(0, "the running handler is not cancelled. Log:\n{0}", Log);
            probe.Completed.Should().BeGreaterThan(0);
            _log.Should().Contain(line => line.Contains("background notifications not handled") && line.Contains("Without persistence, they are lost"), "Log:\n{0}", Log);
            _log.Should().NotContain(line => line.Contains("Could not create a DI scope"), "no queued notification is started once disposal began. Log:\n{0}", Log);
        }

        [Fact]
        public async Task NotificationsLeftInStorage_ShouldBeRecoveredWhenTheHostStarts_WithoutAnyPublish()
        {
            // The publisher, and with it the recovery loop, used to start only on the first Publish, so after a crash the
            // stored notifications waited until something else was published.
            using (var store = new FileNotificationPersistence(_directory))
            {
                var leftover = new Counted();
                var serializer = new JsonNotificationSerializer();
                await store.PersistAsync(new NotificationWorkItem(leftover, typeof(Counted), DateTime.UtcNow, serializer.Serialize(leftover, typeof(Counted))));
            }

            var probe = new Probe();
            using var host = BuildHost(probe, services => services.AddTransient<INotificationHandler<Counted>, CountedHandler>(), persistence: true);
            await host.StartAsync();
            await Eventually.WaitUntilAsync(() => probe.Completed == 1 && PersistedFiles().Length == 0);
            await host.StopAsync();

            probe.Completed.Should().Be(1, "Log:\n{0}", Log);
            PersistedFiles().Should().BeEmpty();
        }

        [Fact]
        public async Task Shutdown_WithAHandlerThatOutlivesTheTimeout_ShouldNotFailOrLogDisposedErrors()
        {
            // After the shutdown timeout the cancellation source and locks were disposed while a worker was still in a
            // handler; when it finished, settling the result threw ObjectDisposedException.
            var probe = new Probe();
            var provider = Build(probe, services =>
                services.AddTransient<INotificationHandler<Stubborn>, StubbornHandler>(),
                persistence: true, shutdownTimeout: TimeSpan.FromMilliseconds(100));

            await provider.GetRequiredService<IMediator>().Publish(new Stubborn());
            await Eventually.WaitUntilAsync(() => probe.Attempts == 1);
            provider.Dispose();
            await Eventually.WaitUntilAsync(() => probe.Completed == 1, TimeSpan.FromSeconds(5));
            await Task.Delay(200);

            probe.Completed.Should().Be(1);
            _log.Should().NotContain(line => line.Contains("ObjectDisposedException") || line.Contains("Cannot access a disposed"), "Log:\n{0}", Log);
        }

        [Fact]
        public void Shutdown_ShouldStopARecoveryScanInProgressAtOnce()
        {
            // Recovery passes used the handlers' token, which is only cancelled at the end of shutdown, so a long scan
            // held shutdown up for the full grace period (and could then queue work into the closed queue).
            var probe = new Probe();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(new QueueLoggerProvider(_log)).SetMinimumLevel(LogLevel.Debug));
            services.AddSingleton(probe);
            services.AddSingleton<INotificationPersistence>(_ => new ScanBlocksUntilCancelled(new FileNotificationPersistence(_directory), probe));
            services.AddMediator(options =>
            {
                options.EnablePersistence = true;
                options.NotificationWorkerCount = 1;
                options.ProcessingInterval = TimeSpan.FromMilliseconds(20);
            });
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<INotificationPublisher>();
            SpinWait.SpinUntil(() => probe.Attempts > 0, TimeSpan.FromSeconds(5)).Should().BeTrue("a recovery scan should have started");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            provider.Dispose();

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "Log:\n{0}", Log);
            _log.Should().NotContain(line => line.Contains("Channel full"), "Log:\n{0}", Log);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
            catch (IOException) { }
        }

        private int ResolveFailures() => _log.Count(line => line.Contains("Could not resolve the handlers of notification Unresolvable"));

        private string[] PersistedFiles() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.json") : Array.Empty<string>();

        private ServiceProvider Build(Probe probe, Action<IServiceCollection> handlers, bool persistence = false, int maxRetryAttempts = 3,
            int workers = 1, TimeSpan? shutdownTimeout = null)
        {
            var services = new ServiceCollection();
            Configure(services, probe, handlers, persistence, maxRetryAttempts, workers, shutdownTimeout);
            return services.BuildServiceProvider();
        }

        private IHost BuildHost(Probe probe, Action<IServiceCollection> handlers, bool persistence = false)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            Configure(builder.Services, probe, handlers, persistence, maxRetryAttempts: 3, workers: 1, shutdownTimeout: null);
            return builder.Build();
        }

        private void Configure(IServiceCollection services, Probe probe, Action<IServiceCollection> handlers, bool persistence, int maxRetryAttempts,
            int workers, TimeSpan? shutdownTimeout)
        {
            services.AddLogging(builder => builder.AddProvider(new QueueLoggerProvider(_log)).SetMinimumLevel(LogLevel.Debug));
            services.AddSingleton(probe);
            handlers(services);
            if (persistence)
                services.AddSingleton<INotificationPersistence>(_ => new FileNotificationPersistence(_directory));
            services.AddMediator(options =>
            {
                options.EnablePersistence = persistence;
                options.NotificationWorkerCount = workers;
                options.ProcessingInterval = TimeSpan.FromMilliseconds(50);
                options.MaxRetryAttempts = maxRetryAttempts;
                options.InitialRetryDelay = TimeSpan.FromMilliseconds(20);
                options.RetryDelayMultiplier = 1.0;
                if (shutdownTimeout is { } timeout) options.ShutdownTimeout = timeout;
            });
        }

        /// <summary>A store whose recovery scan only ends when its token is cancelled, like a slow scan of a large store.</summary>
        private sealed class ScanBlocksUntilCancelled(INotificationPersistence inner, Probe probe) : INotificationPersistence
        {
            public Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default) => inner.PersistAsync(workItem, cancellationToken);
            public async Task<IEnumerable<PersistedNotificationWorkItem>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
            {
                probe.Attempt();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return Array.Empty<PersistedNotificationWorkItem>();
            }
            public Task CompleteAsync(string id, CancellationToken cancellationToken = default) => inner.CompleteAsync(id, cancellationToken);
            public Task FailAsync(string id, Exception exception, DateTime? retryAfter = null, CancellationToken cancellationToken = default) => inner.FailAsync(id, exception, retryAfter, cancellationToken);
            public Task CleanupAsync(DateTime olderThan, CancellationToken cancellationToken = default) => inner.CleanupAsync(olderThan, cancellationToken);
            public void Dispose() => inner.Dispose();
        }

        public sealed class Probe
        {
            private int _attempts, _completed, _failed, _disposed;
            public int Attempts => Volatile.Read(ref _attempts);
            public int Completed => Volatile.Read(ref _completed);
            public int Failed => Volatile.Read(ref _failed);
            public int Disposed => Volatile.Read(ref _disposed);
            public void Attempt() => Interlocked.Increment(ref _attempts);
            public void Complete() => Interlocked.Increment(ref _completed);
            public void Fail() => Interlocked.Increment(ref _failed);
            public void Dispose() => Interlocked.Increment(ref _disposed);
        }

        /// <summary>Throws when used by two callers at once, like a DbContext.</summary>
        public sealed class NotThreadSafeService
        {
            private int _inUse;

            public async Task UseAsync()
            {
                if (Interlocked.Exchange(ref _inUse, 1) == 1)
                    throw new InvalidOperationException("A second operation was started on this instance before a previous operation completed.");
                await Task.Delay(100);
                Volatile.Write(ref _inUse, 0);
            }
        }

        public sealed class Shared : INotification;

        public abstract class SharedHandlerBase(NotThreadSafeService service, Probe probe) : INotificationHandler<Shared>
        {
            public async Task Handle(Shared notification, CancellationToken cancellationToken)
            {
                try { await service.UseAsync(); probe.Complete(); }
                catch { probe.Fail(); throw; }
            }
        }

        public sealed class FirstSharedHandler(NotThreadSafeService service, Probe probe) : SharedHandlerBase(service, probe);

        public sealed class SecondSharedHandler(NotThreadSafeService service, Probe probe) : SharedHandlerBase(service, probe);

        public sealed class Unresolvable : INotification;

        public interface IMissingDependency;

        /// <summary>Depends on a service nobody registered, so resolving it always fails.</summary>
        public sealed class UnresolvableHandler(IMissingDependency dependency) : INotificationHandler<Unresolvable>
        {
            public Task Handle(Unresolvable notification, CancellationToken cancellationToken) { _ = dependency; return Task.CompletedTask; }
        }

        public sealed class UsesAsyncDisposable : INotification;

        public sealed class AsyncOnlyDisposable(Probe probe) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { probe.Dispose(); return ValueTask.CompletedTask; }
        }

        public sealed class AsyncDisposableHandler(AsyncOnlyDisposable service, Probe probe) : INotificationHandler<UsesAsyncDisposable>
        {
            public Task Handle(UsesAsyncDisposable notification, CancellationToken cancellationToken)
            {
                _ = service;
                probe.Complete();
                return Task.CompletedTask;
            }
        }

        public sealed class Counted : INotification;

        public sealed class CountedHandler(Probe probe) : INotificationHandler<Counted>
        {
            public Task Handle(Counted notification, CancellationToken cancellationToken) { probe.Complete(); return Task.CompletedTask; }
        }

        public sealed class Slow : INotification;

        public sealed class SlowHandler(Probe probe) : INotificationHandler<Slow>
        {
            public async Task Handle(Slow notification, CancellationToken cancellationToken)
            {
                probe.Attempt();
                try { await Task.Delay(20, cancellationToken); probe.Complete(); }
                catch { probe.Fail(); throw; }
            }
        }

        public sealed class Stubborn : INotification;

        /// <summary>Ignores cancellation and outlives the shutdown timeout.</summary>
        public sealed class StubbornHandler(Probe probe) : INotificationHandler<Stubborn>
        {
            public async Task Handle(Stubborn notification, CancellationToken cancellationToken)
            {
                probe.Attempt();
                await Task.Delay(TimeSpan.FromSeconds(2.5), CancellationToken.None);
                probe.Complete();
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
                    => log.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel}: {formatter(state, null)}{(exception is null ? "" : " " + exception.GetType().Name + ": " + exception.Message)}");
            }
        }
    }
}
