using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mediator.Tests
{
    /// <summary>
    /// Time from Publish until the background handler has actually run (after a warm-up, so one-time JIT and
    /// initialization are excluded). The thresholds are deliberately generous; the point is to catch large regressions
    /// and to prove the handlers really ran.
    /// </summary>
    public class NotificationPerformanceTests
    {
        [Fact]
        public async Task SingleNotification_ShouldProcessQuickly()
        {
            using var serviceProvider = Build();
            var mediator = serviceProvider.GetRequiredService<IMediator>();
            var counter = new HandledCounter();

            await mediator.Publish(new QuickTestNotification { Message = "Warmup", Counter = counter });
            await Eventually.WaitUntilAsync(() => counter.Count == 1);

            var sw = Stopwatch.StartNew();
            await mediator.Publish(new QuickTestNotification { Message = "Test", Counter = counter });
            await Eventually.WaitUntilAsync(() => counter.Count == 2);
            sw.Stop();

            counter.Count.Should().Be(2, "the handler must have run for the measured notification");
            sw.Elapsed.TotalMilliseconds.Should().BeLessThan(500, "publish to handled should be fast once warm");
        }

        [Fact]
        public async Task MultipleNotifications_ShouldProcessEfficiently()
        {
            using var serviceProvider = Build();
            var mediator = serviceProvider.GetRequiredService<IMediator>();
            var counter = new HandledCounter();

            await mediator.Publish(new QuickTestNotification { Message = "Warmup", Counter = counter });
            await Eventually.WaitUntilAsync(() => counter.Count == 1);

            const int notificationCount = 10;
            var sw = Stopwatch.StartNew();

            var tasks = new Task[notificationCount];
            for (int i = 0; i < notificationCount; i++)
            {
                tasks[i] = mediator.Publish(new QuickTestNotification { Message = $"Test {i}", Counter = counter });
            }

            await Task.WhenAll(tasks);
            await Eventually.WaitUntilAsync(() => counter.Count == notificationCount + 1);
            sw.Stop();

            counter.Count.Should().Be(notificationCount + 1, "every notification must have been handled");
            (sw.Elapsed.TotalMilliseconds / notificationCount).Should().BeLessThan(100, "average time per notification once warm");
        }

        private static ServiceProvider Build()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.AddTransient<INotificationHandler<QuickTestNotification>, QuickTestHandler>();
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 1;
                options.ChannelCapacity = 100;
            });
            return services.BuildServiceProvider();
        }
    }

    public sealed class HandledCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    public class QuickTestNotification : INotification
    {
        public string Message { get; set; } = string.Empty;
        public HandledCounter? Counter { get; set; }
    }

    public class QuickTestHandler : INotificationHandler<QuickTestNotification>
    {
        public Task Handle(QuickTestNotification notification, CancellationToken cancellationToken)
        {
            notification.Counter?.Increment();
            return Task.CompletedTask;
        }
    }
}
