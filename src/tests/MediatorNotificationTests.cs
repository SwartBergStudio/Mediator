namespace Mediator.Tests
{
    public class MediatorNotificationTests
    {
        [Fact]
        public async Task Publish_ShouldBroadcastToAllHandlers()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();
            
            using (serviceProvider)
            {
                var notification = new TestEvent { Message = "Broadcast test" };

                await mediator.Publish(notification);
                await Task.Delay(100);

                tracker.GetCounter("EventHandler1").Should().Be(1);
                tracker.GetCounter("EventHandler2").Should().Be(1);
                tracker.GetCounter("FailingEventHandler").Should().Be(1);
            }
        }

        [Fact]
        public async Task Publish_WithPersistence_ShouldProcessEventually()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking(options =>
            {
                options.EnablePersistence = true;
                options.ProcessingInterval = TimeSpan.FromMilliseconds(100);
            });
            
            using (serviceProvider)
            {
                var notification = new TestEvent { Message = "Persistence test" };

                await mediator.Publish(notification);
                await Task.Delay(300);

                tracker.GetCounter("EventHandler1").Should().BeGreaterThan(0);
                tracker.GetCounter("EventHandler2").Should().BeGreaterThan(0);
            }
        }

        [Fact]
        public async Task Publish_HighVolumeEvents_ShouldHandleEfficiently()
        {
            var (mediator, serviceProvider, tracker) = TestFactory.CreateMediatorWithTracking();
            
            using (serviceProvider)
            {
                const int eventCount = 500;
                var events = Enumerable.Range(0, eventCount)
                    .Select(i => new TestEvent { Message = $"Event {i}" })
                    .ToArray();

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var tasks = events.Select(e => mediator.Publish(e));
                await Task.WhenAll(tasks);
                await Task.Delay(1000);
                stopwatch.Stop();

                tracker.GetCounter("EventHandler1").Should().Be(eventCount);
                tracker.GetCounter("EventHandler2").Should().Be(eventCount);
                stopwatch.ElapsedMilliseconds.Should().BeLessThan(10000);
            }
        }
    }
}