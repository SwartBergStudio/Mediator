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
}
