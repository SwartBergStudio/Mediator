namespace Mediator.Tests
{
    public class MediatorNotificationTests
    {
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
