using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    public class DomainEventHandlerDiscoveryTests
    {
        [Fact]
        public async Task Publish_DomainEventsList_WhenPublishedAsBaseType_ShouldInvokeHandler()
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));

            var tracker = new TestTracker();
            services.AddSingleton(tracker);

            services.AddMediator(options => {
                options.EnablePersistence = false;
                options.NotificationWorkerCount = 1;
            }, typeof(DomainEventHandlerDiscoveryTests).Assembly);

            using var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();

            var entity = new MyObject();

            var domainEvents = new List<DomainEvent>
            {
                new CreatedEvent<MyObject>(entity)
            };

            foreach (var de in domainEvents)
            {
                await mediator.Publish(de);
            }

            await TestHelpers.WaitForCounterAsync(tracker, "MyObjectCreated", TimeSpan.FromSeconds(2));
            tracker.GetCounter("MyObjectCreated").Should().BeGreaterThan(0);
        }
    }

    public abstract class DomainEvent : INotification
    {
        public DateTimeOffset OccurredOn { get; protected set; } = DateTimeOffset.UtcNow;
    }

    public class CreatedEvent<T> : DomainEvent
    {
        public T Entity { get; }
        public CreatedEvent(T entity) { Entity = entity; }
    }

    public class BaseEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    public class MyObject : BaseEntity { }

    public class MyObjectCreatedNotificationHandler : INotificationHandler<CreatedEvent<MyObject>>
    {
        private readonly TestTracker _tracker;
        public MyObjectCreatedNotificationHandler(TestTracker tracker) => _tracker = tracker;
        public Task Handle(CreatedEvent<MyObject> notification, CancellationToken cancellationToken)
        {
            _tracker.IncrementCounter("MyObjectCreated");
            return Task.CompletedTask;
        }
    }

    
    internal static class TestHelpers
    {
        public static async Task WaitForCounterAsync(TestTracker tracker, string key, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (tracker.GetCounter(key) > 0) return;
                await Task.Delay(25);
            }
        }
    }
}