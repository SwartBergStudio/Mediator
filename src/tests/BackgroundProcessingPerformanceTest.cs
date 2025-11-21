using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Mediator.Tests
{
    /// <summary>
    /// Simple performance test to measure background notification processing delays
    /// Focuses on the specific issue: 6-second delays with small notification volumes
    /// </summary>
    public class BackgroundProcessingPerformanceTest
    {
        [Fact]
        public async Task SmallVolumeNotifications_ShouldProcessQuickly()
        {
            // Arrange: Setup similar to your test environment
            var tracker = new SimpleNotificationTracker();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.AddSingleton(tracker);
            
            // Register notification handlers (simulating email scenario)
            services.AddTransient<INotificationHandler<TestEmailNotification>, TestEmailHandler>();
            services.AddTransient<INotificationHandler<TestEmailNotification>, TestLoggingHandler>();
            services.AddTransient<INotificationHandler<TestEmailNotification>, TestMetricsHandler>();
            
            // Configure mediator similar to test environment
            services.AddMediator(options =>
            {
                options.EnablePersistence = false; // No persistence as mentioned in investigation
                options.NotificationWorkerCount = 1; // Test environment setting
                options.ChannelCapacity = 100;
                options.UseConfigureAwaitGlobally = true;
            });

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();
            
            // Act: Publish 10 notifications (small volume scenario)
            var stopwatch = Stopwatch.StartNew();
            
            for (int i = 0; i < 10; i++)
            {
                await mediator.Publish(new TestEmailNotification
                {
                    To = $"test{i}@example.com",
                    Subject = $"Test Email {i}",
                    Body = "Test email body"
                });
            }

            // Wait for all notifications to be processed
            var timeout = TimeSpan.FromSeconds(10); // Should be much faster than this
            var deadline = DateTime.UtcNow.Add(timeout);
            
            while (tracker.ProcessedCount < 30 && DateTime.UtcNow < deadline) // 10 notifications × 3 handlers = 30
            {
                await Task.Delay(10);
            }
            
            stopwatch.Stop();

            // Assert: Should process much faster than 6 seconds
            var actualTime = stopwatch.Elapsed;
            var processedCount = tracker.ProcessedCount;
            
            // Output diagnostics for analysis
            Console.WriteLine($"Processed {processedCount}/30 notifications in {actualTime.TotalMilliseconds:F1}ms");
            Console.WriteLine($"Average time per notification: {(actualTime.TotalMilliseconds / 10):F1}ms");
            
            // The test should complete in under 1 second for 10 notifications
            Assert.True(actualTime.TotalSeconds < 1.0, 
                $"Performance issue detected: 10 notifications took {actualTime.TotalSeconds:F1} seconds (expected < 1.0s). Processed {processedCount}/30 handlers.");
            
            Assert.Equal(30, processedCount); // All notifications should be processed
        }

        [Fact]
        public async Task SingleNotification_EndToEndLatency()
        {
            // Arrange: Minimal setup to measure single notification latency
            var tracker = new SimpleNotificationTracker();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.AddSingleton(tracker);
            
            services.AddTransient<INotificationHandler<TestEmailNotification>, TestEmailHandler>();
            
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 1;
                options.ChannelCapacity = 100;
            });

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();
            
            // Act: Single notification with precise timing
            var stopwatch = Stopwatch.StartNew();
            
            await mediator.Publish(new TestEmailNotification
            {
                To = "single@example.com",
                Subject = "Single Test",
                Body = "Single notification test"
            });

            // Wait for processing with timeout
            var timeout = DateTime.UtcNow.AddMilliseconds(2000);
            while (tracker.ProcessedCount < 1 && DateTime.UtcNow < timeout)
            {
                await Task.Delay(1);
            }
            
            stopwatch.Stop();

            // Assert: Single notification should be very fast
            var latency = stopwatch.Elapsed;
            Console.WriteLine($"Single notification end-to-end latency: {latency.TotalMilliseconds:F1}ms");
            
            Assert.True(latency.TotalMilliseconds < 100, 
                $"Single notification latency too high: {latency.TotalMilliseconds:F1}ms (expected < 100ms)");
            
            Assert.Equal(1, tracker.ProcessedCount);
        }

        [Fact]
        public async Task ChannelQueueingPerformance()
        {
            // Arrange: Test channel queuing speed separately from handler execution
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            
            // Use no-op handler to isolate channel performance
            services.AddTransient<INotificationHandler<TestEmailNotification>, NoOpHandler>();
            
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 1;
                options.ChannelCapacity = 1000;
            });

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();
            
            // Act: Measure pure queuing speed (publish operations only)
            var stopwatch = Stopwatch.StartNew();
            
            var publishTasks = new Task[20];
            for (int i = 0; i < 20; i++)
            {
                publishTasks[i] = mediator.Publish(new TestEmailNotification
                {
                    To = $"queue{i}@example.com",
                    Subject = $"Queue Test {i}",
                    Body = "Queue performance test"
                });
            }
            
            await Task.WhenAll(publishTasks);
            stopwatch.Stop();

            // Assert: Channel queuing should be very fast
            var queuingTime = stopwatch.Elapsed;
            Console.WriteLine($"Channel queuing time for 20 notifications: {queuingTime.TotalMilliseconds:F1}ms");
            
            Assert.True(queuingTime.TotalMilliseconds < 50, 
                $"Channel queuing too slow: {queuingTime.TotalMilliseconds:F1}ms (expected < 50ms)");
        }
    }

    /// <summary>
    /// Simple notification tracker for performance testing
    /// </summary>
    public class SimpleNotificationTracker
    {
        private int _processedCount;
        private readonly List<DateTime> _processingTimes = new();
        private readonly object _lock = new();

        public int ProcessedCount => _processedCount;

        public void RecordProcessing()
        {
            Interlocked.Increment(ref _processedCount);
            lock (_lock)
            {
                _processingTimes.Add(DateTime.UtcNow);
            }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _processedCount, 0);
            lock (_lock)
            {
                _processingTimes.Clear();
            }
        }

        public List<DateTime> GetProcessingTimes()
        {
            lock (_lock)
            {
                return new List<DateTime>(_processingTimes);
            }
        }
    }

    /// <summary>
    /// Test email notification
    /// </summary>
    public class TestEmailNotification : INotification
    {
        public string To { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Email handler with minimal processing time
    /// </summary>
    public class TestEmailHandler : INotificationHandler<TestEmailNotification>
    {
        private readonly SimpleNotificationTracker _tracker;

        public TestEmailHandler(SimpleNotificationTracker tracker)
        {
            _tracker = tracker;
        }

        public async Task Handle(TestEmailNotification notification, CancellationToken cancellationToken)
        {
            // Simulate minimal email service work
            await Task.Delay(1, cancellationToken);
            _tracker.RecordProcessing();
        }
    }

    /// <summary>
    /// Logging handler (synchronous)
    /// </summary>
    public class TestLoggingHandler : INotificationHandler<TestEmailNotification>
    {
        private readonly SimpleNotificationTracker _tracker;

        public TestLoggingHandler(SimpleNotificationTracker tracker)
        {
            _tracker = tracker;
        }

        public Task Handle(TestEmailNotification notification, CancellationToken cancellationToken)
        {
            // Simulate logging (synchronous)
            var logEntry = $"Email to {notification.To}";
            _ = logEntry.Length;
            _tracker.RecordProcessing();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Metrics handler (synchronous)
    /// </summary>
    public class TestMetricsHandler : INotificationHandler<TestEmailNotification>
    {
        private readonly SimpleNotificationTracker _tracker;

        public TestMetricsHandler(SimpleNotificationTracker tracker)
        {
            _tracker = tracker;
        }

        public Task Handle(TestEmailNotification notification, CancellationToken cancellationToken)
        {
            // Simulate metrics collection
            _ = notification.Body?.Length ?? 0;
            _tracker.RecordProcessing();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// No-op handler for isolating channel performance
    /// </summary>
    public class NoOpHandler : INotificationHandler<TestEmailNotification>
    {
        public Task Handle(TestEmailNotification notification, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}