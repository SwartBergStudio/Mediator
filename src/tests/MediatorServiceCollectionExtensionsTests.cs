using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Tests
{
    public class MediatorServiceCollectionExtensionsTests
    {
        [Fact]
        public void AddMediator_WithAssemblies_ShouldRegisterRequiredServices()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            services.AddMediator(typeof(SimpleTestRequestHandler).Assembly);

            using var serviceProvider = services.BuildServiceProvider();

            serviceProvider.GetService<IMediator>().Should().NotBeNull();
            serviceProvider.GetService<IRequestHandler<SimpleTestRequest, string>>().Should().NotBeNull();
            serviceProvider.GetService<INotificationHandler<SimpleTestNotification>>().Should().NotBeNull();
        }

        [Fact]
        public void AddMediator_WithCustomPersistence_ShouldUseCustomImplementation()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton<INotificationPersistence, MockNotificationPersistence>();
            services.AddMediator(typeof(SimpleTestRequestHandler).Assembly);

            using var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<INotificationPersistence>().Should().BeOfType<MockNotificationPersistence>();
        }

        [Fact]
        public void AddMediator_WithCustomSerializer_ShouldUseCustomImplementation()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton<INotificationSerializer, MockNotificationSerializer>();
            services.AddMediator(typeof(SimpleTestRequestHandler).Assembly);

            using var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<INotificationSerializer>().Should().BeOfType<MockNotificationSerializer>();
        }

        [Fact]
        public void AddMediator_ShouldRegisterAllHandlerTypes()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            services.AddMediator(typeof(SimpleTestRequestHandler).Assembly);

            using var serviceProvider = services.BuildServiceProvider();

            serviceProvider.GetService<IRequestHandler<SimpleTestRequest, string>>().Should().BeOfType<SimpleTestRequestHandler>();
            serviceProvider.GetService<IRequestHandler<SimpleTestRequestWithoutResponse>>().Should().BeOfType<SimpleTestRequestWithoutResponseHandler>();

            var notificationHandlers = serviceProvider.GetServices<INotificationHandler<SimpleTestNotification>>();
            notificationHandlers.Should().HaveCount(1);
        }

        [Fact]
        public void AddMediator_WithoutAssemblies_ShouldStillRegisterCore()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            services.AddMediator();

            using var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<IMediator>().Should().NotBeNull();
        }

        [Fact]
        public void AddMediator_CalledTwiceWithSameAssembly_ShouldNotDuplicateHandlers()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            var assembly = typeof(SimpleTestRequestHandler).Assembly;
            services.AddMediator(assembly);
            services.AddMediator(assembly);

            using var serviceProvider = services.BuildServiceProvider();

            var notificationHandlers = serviceProvider.GetServices<INotificationHandler<SimpleTestNotification>>();
            notificationHandlers.Should().HaveCount(1, "calling AddMediator twice should not duplicate notification handlers");

            var requestHandlers = serviceProvider.GetServices<IRequestHandler<SimpleTestRequest, string>>();
            requestHandlers.Should().HaveCount(1, "calling AddMediator twice should not duplicate request handlers");

            var commandHandlers = serviceProvider.GetServices<IRequestHandler<SimpleTestRequestWithoutResponse>>();
            commandHandlers.Should().HaveCount(1, "calling AddMediator twice should not duplicate command handlers");
        }

        [Fact]
        public void AddMediator_WithManualRegistrationAndAssemblyScan_ShouldNotDuplicateHandlers()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            // Manual registration first
            services.AddTransient<INotificationHandler<SimpleTestNotification>, SimpleTestNotificationHandlerForRegistration>();

            // Then assembly scan that finds the same handler
            services.AddMediator(typeof(SimpleTestNotificationHandlerForRegistration).Assembly);

            using var serviceProvider = services.BuildServiceProvider();

            var notificationHandlers = serviceProvider.GetServices<INotificationHandler<SimpleTestNotification>>();
            notificationHandlers.Should().HaveCount(1, "manual registration + assembly scan should not duplicate notification handlers");
        }

        [Fact]
        public void AddMediator_WithConfiguration_ShouldApplyOptions()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            services.AddMediator(options =>
            {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 5;
            }, typeof(SimpleTestRequestHandler).Assembly);

            using var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetService<IMediator>().Should().NotBeNull();

            var optionsSnapshot = serviceProvider.GetRequiredService<IOptions<MediatorOptions>>();
            optionsSnapshot.Value.EnablePersistence.Should().BeFalse();
            optionsSnapshot.Value.NotificationWorkerCount.Should().Be(5);
        }
    }

    public class MockNotificationPersistence : INotificationPersistence
    {
        public Task CleanupAsync(DateTime olderThan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FailAsync(string id, Exception exception, DateTime? retryAfter = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IEnumerable<Persistence.PersistedNotificationWorkItem>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Empty<Persistence.PersistedNotificationWorkItem>());
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
