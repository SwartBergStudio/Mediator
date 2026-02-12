namespace Mediator.Tests
{
    public class MediatorOptionsTests
    {
        [Fact]
        public void DefaultOptions_ShouldHaveCorrectValues()
        {
            var options = new MediatorOptions();

            options.NotificationWorkerCount.Should().Be(Environment.ProcessorCount);
            options.MaxRetryAttempts.Should().Be(3);
            options.InitialRetryDelay.Should().Be(TimeSpan.FromMinutes(1));
            options.RetryDelayMultiplier.Should().Be(2.0);
            options.ProcessingInterval.Should().Be(TimeSpan.FromMinutes(1));
            options.ProcessingBatchSize.Should().Be(100);
            options.CleanupRetentionPeriod.Should().Be(TimeSpan.FromDays(7));
            options.CleanupInterval.Should().Be(TimeSpan.FromHours(6));
            options.EnablePersistence.Should().BeTrue();
            options.ChannelCapacity.Should().Be(1000);
            options.UseConfigureAwaitGlobally.Should().BeTrue();
        }
    }
}
