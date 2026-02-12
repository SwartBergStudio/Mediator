using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mediator.Tests
{
    /// <summary>
    /// Tests to verify notification performance improvements are working correctly
    /// </summary>
    public class NotificationPerformanceTests
    {
        [Fact]
        public async Task SingleNotification_ShouldProcessQuickly()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.AddTransient<INotificationHandler<QuickTestNotification>, QuickTestHandler>();
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 1;
                options.ChannelCapacity = 100;
                options.UseConfigureAwaitGlobally = true;
            });

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            await mediator.Publish(new QuickTestNotification { Message = "Warmup" });
            await Task.Delay(10);

            var sw = Stopwatch.StartNew();
            await mediator.Publish(new QuickTestNotification { Message = "Test" });
            await Task.Delay(50);
            sw.Stop();

            Console.WriteLine($"Single notification processing time: {sw.Elapsed.TotalMilliseconds:F2}ms");
            
            Assert.True(sw.Elapsed.TotalMilliseconds < 100, 
                $"Single notification took too long: {sw.Elapsed.TotalMilliseconds:F2}ms");
        }

        [Fact]
        public async Task MultipleNotifications_ShouldProcessEfficiently()
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

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            await mediator.Publish(new QuickTestNotification { Message = "Warmup" });
            await Task.Delay(10);

            const int notificationCount = 10;
            var sw = Stopwatch.StartNew();

            var tasks = new Task[notificationCount];
            for (int i = 0; i < notificationCount; i++)
            {
                tasks[i] = mediator.Publish(new QuickTestNotification { Message = $"Test {i}" });
            }

            await Task.WhenAll(tasks);
            await Task.Delay(50);
            sw.Stop();

            var avgTimePerNotification = sw.Elapsed.TotalMilliseconds / notificationCount;
            
            Console.WriteLine($"Total time for {notificationCount} notifications: {sw.Elapsed.TotalMilliseconds:F2}ms");
            Console.WriteLine($"Average time per notification: {avgTimePerNotification:F2}ms");

            Assert.True(avgTimePerNotification < 50, 
                $"Average notification time too high: {avgTimePerNotification:F2}ms per notification");
        }

    }

    public class QuickTestNotification : INotification
    {
        public string Message { get; set; } = string.Empty;
    }

    public class QuickTestHandler : INotificationHandler<QuickTestNotification>
    {
        public Task Handle(QuickTestNotification notification, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}