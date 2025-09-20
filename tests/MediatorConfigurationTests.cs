using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Tests
{
    public class MediatorConfigurationTests
    {
        [Fact]
        public void AddMediator_WithDefaultOptions_ShouldUseDefaults()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Act
            services.AddMediator(typeof(TestRequestHandler).Assembly);

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetService<IMediator>();
            mediator.Should().NotBeNull();
        }

        [Fact]
        public void AddMediator_WithCustomOptions_ShouldApplyOptions()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Act
            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 5;
            }, typeof(TestRequestHandler).Assembly);

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetService<IMediator>();
            mediator.Should().NotBeNull();
            
            var optionsSnapshot = serviceProvider.GetService<IOptions<MediatorOptions>>();
            optionsSnapshot.Should().NotBeNull();
            optionsSnapshot!.Value.EnablePersistence.Should().BeFalse();
            optionsSnapshot.Value.NotificationWorkerCount.Should().Be(5);
        }

        [Fact]
        public void AddMediator_WithCustomPersistence_ShouldUseCustomPersistence()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Act
            services.AddSingleton<INotificationPersistence, CustomNotificationPersistence>();
            services.AddMediator(typeof(TestRequestHandler).Assembly);

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            var persistence = serviceProvider.GetService<INotificationPersistence>();
            persistence.Should().BeOfType<CustomNotificationPersistence>();
        }

        [Fact]
        public void AddMediator_WithCustomSerializer_ShouldUseCustomSerializer()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Act
            services.AddSingleton<INotificationSerializer, CustomNotificationSerializer>();
            services.AddMediator(typeof(TestRequestHandler).Assembly);

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            var serializer = serviceProvider.GetService<INotificationSerializer>();
            serializer.Should().BeOfType<CustomNotificationSerializer>();
        }
    }

    public class CustomNotificationPersistence : INotificationPersistence
    {
        public Task CleanupAsync(DateTime olderThan, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task CompleteAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task FailAsync(string id, Exception exception, DateTime? retryAfter = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IEnumerable<Persistence.PersistedNotificationWorkItem>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Enumerable.Empty<Persistence.PersistedNotificationWorkItem>());
        }

        public Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Guid.NewGuid().ToString());
        }

        public void Dispose() { }
    }

    public class CustomNotificationSerializer : INotificationSerializer
    {
        public object? Deserialize(string serializedNotification, Type notificationType)
        {
            return Activator.CreateInstance(notificationType);
        }

        public string Serialize(object notification, Type notificationType)
        {
            return "{}";
        }
    }
}