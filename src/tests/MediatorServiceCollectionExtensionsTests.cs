using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    public class MediatorServiceCollectionExtensionsTests
    {
        [Fact]
        public void AddMediator_WithAssemblies_ShouldRegisterRequiredServices()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Act
            services.AddMediator(typeof(SimpleTestRequestHandler).Assembly);

            // Assert
            using var serviceProvider = services.BuildServiceProvider();
            
            serviceProvider.GetService<IMediator>().Should().NotBeNull();
            serviceProvider.GetService<IRequestHandler<SimpleTestRequest, string>>().Should().NotBeNull();
            serviceProvider.GetService<INotificationHandler<SimpleTestNotification>>().Should().NotBeNull();
        }

        [Fact]
        public void AddMediator_WithCustomPersistence_ShouldRegisterCustomPersistence()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton<INotificationPersistence, MockNotificationPersistence>();
            services.AddMediator(typeof(SimpleTestRequestHandler).Assembly);

            // Act & Assert
            using var serviceProvider = services.BuildServiceProvider();
            var persistence = serviceProvider.GetService<INotificationPersistence>();
            persistence.Should().BeOfType<MockNotificationPersistence>();
        }

        [Fact]
        public void AddMediator_WithCustomSerializer_ShouldRegisterCustomSerializer()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton<INotificationSerializer, MockNotificationSerializer>();
            services.AddMediator(typeof(SimpleTestRequestHandler).Assembly);

            // Act & Assert
            using var serviceProvider = services.BuildServiceProvider();
            var serializer = serviceProvider.GetService<INotificationSerializer>();
            serializer.Should().BeOfType<MockNotificationSerializer>();
        }

        [Fact]
        public void AddMediator_ShouldRegisterAllHandlerTypes()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Act
            services.AddMediator(typeof(SimpleTestRequestHandler).Assembly);

            // Assert
            using var serviceProvider = services.BuildServiceProvider();
            
            // Request handlers
            serviceProvider.GetService<IRequestHandler<SimpleTestRequest, string>>().Should().BeOfType<SimpleTestRequestHandler>();
            serviceProvider.GetService<IRequestHandler<SimpleTestRequestWithoutResponse>>().Should().BeOfType<SimpleTestRequestWithoutResponseHandler>();
            
            // Notification handlers
            var notificationHandlers = serviceProvider.GetServices<INotificationHandler<SimpleTestNotification>>();
            notificationHandlers.Should().HaveCount(1); // Should have our simple handler
        }

        [Fact]
        public void AddMediator_WithoutAssemblies_ShouldStillRegisterCore()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Act
            services.AddMediator();

            // Assert
            using var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<IMediator>().Should().NotBeNull();
        }

        [Theory]
        [InlineData(typeof(IRequestHandler<SimpleTestRequest, string>))]
        [InlineData(typeof(IRequestHandler<SimpleTestRequestWithoutResponse>))]
        public void AddMediator_ShouldRegisterHandlerInterfaces(Type expectedInterface)
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Act
            services.AddMediator(typeof(SimpleTestRequestHandler).Assembly);

            // Assert
            using var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService(expectedInterface).Should().NotBeNull();
        }
        
        [Fact]
        public void AddMediator_ShouldRegisterNotificationHandlerInterface()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Act
            services.AddMediator(typeof(SimpleTestRequestHandler).Assembly);

            // Assert
            using var serviceProvider = services.BuildServiceProvider();
            var handlers = serviceProvider.GetServices<INotificationHandler<SimpleTestNotification>>();
            handlers.Should().NotBeEmpty();
        }

        [Fact]
        public void AddMediator_WithConfiguration_ShouldApplyConfiguration()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Act
            services.AddMediator(options => 
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 5;
                options.MaxRetryAttempts = 10;
            }, typeof(SimpleTestRequestHandler).Assembly);

            // Assert
            using var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<IMediator>().Should().NotBeNull();
        }
    }

    // Mock implementations for testing
    public class MockNotificationPersistence : INotificationPersistence
    {
        public Task CleanupAsync(DateTime olderThan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(string id, Exception exception, DateTime? retryAfter = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IEnumerable<Persistence.PersistedNotificationWorkItem>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Enumerable.Empty<Persistence.PersistedNotificationWorkItem>());
        }
        public Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid().ToString());
        public void Dispose() { }
    }

    public class MockNotificationSerializer : INotificationSerializer
    {
        public object? Deserialize(string? serializedNotification, Type notificationType)
        {
            if (string.IsNullOrWhiteSpace(serializedNotification))
                return null;
            return Activator.CreateInstance(notificationType);
        }
        public string Serialize(object? notification, Type notificationType)
        {
            if (notification is null)
                return "null";
            return "{}";
        }
    }
}