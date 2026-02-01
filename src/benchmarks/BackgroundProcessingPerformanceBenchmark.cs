using BenchmarkDotNet.Attributes;

namespace Mediator.Benchmarks
{
    /// <summary>
    /// Benchmarks to measure background notification processing performance and identify bottlenecks
    /// in channel processing, service resolution, and async patterns.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob]
    public class BackgroundProcessingPerformanceBenchmark
    {
        private ServiceProvider? _serviceProvider;
        private IMediator? _mediator;
        private EmailNotification? _emailNotification;
        [Params(1, 4, 8)] // Test with different worker counts
        public int WorkerCount { get; set; }

        [Params(100, 1000)] // Test with different channel capacities
        public int ChannelCapacity { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
            services.AddTransient<INotificationHandler<EmailNotification>, EmailNotificationHandler>();
            services.AddTransient<INotificationHandler<EmailNotification>, EmailLoggingHandler>();
            services.AddTransient<INotificationHandler<EmailNotification>, EmailMetricsHandler>();
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = WorkerCount;
                options.ChannelCapacity = ChannelCapacity;
                options.UseConfigureAwaitGlobally = true;
            });
            _serviceProvider = services.BuildServiceProvider();
            _mediator = _serviceProvider.GetRequiredService<IMediator>();
            _emailNotification = new EmailNotification
            {
                To = "user@example.com",
                Subject = "Test Email",
                Body = "This is a test email for performance benchmarking."
            };

            for (int i = 0; i < 5; i++)
            {
                _mediator.Publish(new EmailNotification 
                { 
                    To = $"warmup{i}@example.com", 
                    Subject = "Warmup", 
                    Body = "Warmup" 
                }).Wait();
            }
            Task.Delay(20).Wait();
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _serviceProvider?.Dispose();
        }

        /// <summary>
        /// Measures the performance of publishing a single email notification
        /// </summary>
        [Benchmark]
        public async Task PublishSingleEmailNotification()
        {
            await _mediator!.Publish(_emailNotification!);
            await Task.Delay(10);
        }

        /// <summary>
        /// Measures channel throughput with 100 concurrent notifications
        /// </summary>
        [Benchmark]
        public async Task Publish100EmailNotifications()
        {
            var tasks = new Task[100];
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = _mediator!.Publish(new EmailNotification { To = $"user{i}@example.com", Subject = $"Test Email {i}", Body = $"Test body {i}" });
            }

            await Task.WhenAll(tasks);
            await Task.Delay(50);
        }

        /// <summary>
        /// Tests performance under high load with 1000 concurrent notifications
        /// </summary>
        [Benchmark]
        public async Task Publish1000EmailNotifications()
        {
            var tasks = new Task[1000];
            for (int i = 0; i < 1000; i++)
            {
                tasks[i] = _mediator!.Publish(new EmailNotification { To = $"user{i}@example.com", Subject = $"Test Email {i}", Body = $"Test body {i}" });
            }

            await Task.WhenAll(tasks);
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Test notification representing email processing scenarios
    /// </summary>
    public class EmailNotification : INotification
    {
        public string To { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Primary email handler simulating email service with async work
    /// </summary>
    public class EmailNotificationHandler : INotificationHandler<EmailNotification>
    {
        public async Task Handle(EmailNotification notification, CancellationToken cancellationToken)
        {
            // Simulate email service work
            await Task.Delay(1, cancellationToken);
            // Simulate some processing
            var emailData = $"To: {notification.To}, Subject: {notification.Subject}";
            var hash = emailData.GetHashCode();
        }
    }

    /// <summary>
    /// Secondary handler for logging (synchronous)
    /// </summary>
    public class EmailLoggingHandler : INotificationHandler<EmailNotification>
    {
        public Task Handle(EmailNotification notification, CancellationToken cancellationToken)
        {
            // Simulate logging work (synchronous)
            var logEntry = $"Email logged: {notification.To}";
            var hash = logEntry.GetHashCode();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Third handler for metrics collection (synchronous)
    /// </summary>
    public class EmailMetricsHandler : INotificationHandler<EmailNotification>
    {
        public Task Handle(EmailNotification notification, CancellationToken cancellationToken)
        {
            // Simulate metrics collection (synchronous)
            var bodyLength = notification.Body?.Length ?? 0;
            var subjectLength = notification.Subject?.Length ?? 0;
            return Task.CompletedTask;
        }
    }
}