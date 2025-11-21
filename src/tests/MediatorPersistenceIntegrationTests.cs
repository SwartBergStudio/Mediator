using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Mediator.Tests
{
    [Collection("Mediator Integration Tests")]
    public class MediatorPersistenceIntegrationTests : MediatorTestBase
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly IMediator _mediator;
        private readonly string _testDirectory;
        private readonly ITestNotificationTracker _tracker;

        public MediatorPersistenceIntegrationTests()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "mediator-integration-tests", Guid.NewGuid().ToString());
            
            var services = new ServiceCollection();
            
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            
            var tracker = new TestNotificationTracker();
            services.AddSingleton<ITestNotificationTracker>(tracker);
            _tracker = tracker;
            
            services.AddMediator(options => 
            {
                options.EnablePersistence = true;
                options.ProcessingInterval = TimeSpan.FromMilliseconds(100);
                options.CleanupInterval = TimeSpan.FromMilliseconds(200);
                options.MaxRetryAttempts = 2;
                options.InitialRetryDelay = TimeSpan.FromMilliseconds(50);
            }, typeof(TestRequestHandler).Assembly);

            services.AddSingleton<INotificationPersistence>(provider => 
                new FileNotificationPersistence(_testDirectory));

            _serviceProvider = services.BuildServiceProvider();
            _mediator = _serviceProvider.GetRequiredService<IMediator>();
        }

        [Fact]
        public async Task Publish_WithPersistence_ShouldProcessNotificationFromFile()
        {
            // Arrange
            _tracker.Reset();
            var notification = new TestNotification { Message = "Persistence test" };

            // Act
            await _mediator.Publish(notification);
            
            // Wait for background processing
            await Task.Delay(500);

            // Assert
            _tracker.GetHandleCount("TestNotificationHandler").Should().BeGreaterThan(0);
            
            // Verify files are cleaned up after processing
            await Task.Delay(300);
            var remainingFiles = Directory.Exists(_testDirectory) ? Directory.GetFiles(_testDirectory, "*.json") : Array.Empty<string>();
            remainingFiles.Should().BeEmpty("files should be cleaned up after processing");
        }

        [Fact]
        public async Task Publish_WithPersistenceFailure_ShouldFallbackToMemoryProcessing()
        {
            // Arrange
            var mockPersistence = new Mock<INotificationPersistence>();
            mockPersistence.Setup(x => x.PersistAsync(It.IsAny<NotificationWorkItem>(), It.IsAny<CancellationToken>()))
                          .ThrowsAsync(new IOException("Disk full"));

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            
            var tracker = new TestNotificationTracker();
            services.AddSingleton<INotificationPersistence>(mockPersistence.Object);
            services.AddSingleton<ITestNotificationTracker>(tracker);
            
            services.AddMediator(options => 
            {
                options.EnablePersistence = true;
            }, typeof(TestRequestHandler).Assembly);

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            tracker.Reset();
            var notification = new TestNotification { Message = "Fallback test" };

            // Act
            await mediator.Publish(notification);
            await Task.Delay(200);

            // Assert
            tracker.GetHandleCount("TestNotificationHandler").Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task Mediator_Dispose_ShouldCleanupResources()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton<ITestNotificationTracker>(new TestNotificationTracker());
            services.AddMediator(options => 
            {
                options.EnablePersistence = true;
            }, typeof(TestRequestHandler).Assembly);

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            // Act
            await mediator.Publish(new TestNotification { Message = "Before dispose" });
            
            // Dispose should complete without throwing
            serviceProvider.Dispose();

            // Assert - No exceptions should be thrown
            true.Should().BeTrue("Dispose should complete without throwing exceptions");
        }

        [Fact]
        public async Task Publish_WithRetryLogic_ShouldRetryFailedNotifications()
        {
            // Arrange - Create a handler that fails first time, succeeds second time
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            var tracker = new TestNotificationTracker();
            
            services.AddSingleton<ITestNotificationTracker>(tracker);
            services.AddSingleton<INotificationHandler<TestNotification>>(sp => 
                new ConditionalFailingHandler(sp.GetRequiredService<ITestNotificationTracker>()));
                
            services.AddMediator(options => 
            {
                options.EnablePersistence = true;
                options.ProcessingInterval = TimeSpan.FromMilliseconds(100);
                options.MaxRetryAttempts = 3;
                options.InitialRetryDelay = TimeSpan.FromMilliseconds(100);
            }, typeof(TestRequestHandler).Assembly);

            services.AddSingleton<INotificationPersistence>(provider => 
                new FileNotificationPersistence(_testDirectory));

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            // Act
            await mediator.Publish(new TestNotification { Message = "Retry test" });
            
            // Wait for initial attempt and retry
            await Task.Delay(1000);

            // Assert
            tracker.GetHandleCount("ConditionalFailingHandler").Should().BeGreaterThan(1, 
                "handler should have been called multiple times due to retry logic");
        }

        public override void Dispose()
        {
            _serviceProvider?.Dispose();
            
            if (Directory.Exists(_testDirectory))
            {
                try
                {
                    Directory.Delete(_testDirectory, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            
            base.Dispose();
        }
    }

    public class ConditionalFailingHandler : INotificationHandler<TestNotification>
    {
        private readonly ITestNotificationTracker _tracker;
        private int _attemptCount;
        private bool _hasSucceeded = false;

        public ConditionalFailingHandler(ITestNotificationTracker tracker)
        {
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        }

        public int AttemptCount => _attemptCount;

        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            if (notification == null)
                throw new ArgumentNullException(nameof(notification));

            Interlocked.Increment(ref _attemptCount);
            _tracker.IncrementHandleCount("ConditionalFailingHandler");
            
            if (!_hasSucceeded && _attemptCount == 1)
            {
                throw new InvalidOperationException("Simulated failure");
            }

            _hasSucceeded = true;
            return Task.CompletedTask;
        }
    }
}