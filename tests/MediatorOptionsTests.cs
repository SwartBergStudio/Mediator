namespace Mediator.Tests
{
    public class MediatorOptionsTests
    {
        [Fact]
        public void DefaultOptions_ShouldHaveCorrectValues()
        {
            // Arrange & Act
            var options = new MediatorOptions();

            // Assert
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
        }

        [Fact]
        public void Options_ShouldAllowCustomization()
        {
            // Arrange
            var options = new MediatorOptions();

            // Act
            options.NotificationWorkerCount = 5;
            options.MaxRetryAttempts = 10;
            options.InitialRetryDelay = TimeSpan.FromSeconds(30);
            options.RetryDelayMultiplier = 3.0;
            options.ProcessingInterval = TimeSpan.FromSeconds(10);
            options.ProcessingBatchSize = 50;
            options.CleanupRetentionPeriod = TimeSpan.FromHours(48);
            options.CleanupInterval = TimeSpan.FromHours(2);
            options.EnablePersistence = false;

            // Assert
            options.NotificationWorkerCount.Should().Be(5);
            options.MaxRetryAttempts.Should().Be(10);
            options.InitialRetryDelay.Should().Be(TimeSpan.FromSeconds(30));
            options.RetryDelayMultiplier.Should().Be(3.0);
            options.ProcessingInterval.Should().Be(TimeSpan.FromSeconds(10));
            options.ProcessingBatchSize.Should().Be(50);
            options.CleanupRetentionPeriod.Should().Be(TimeSpan.FromHours(48));
            options.CleanupInterval.Should().Be(TimeSpan.FromHours(2));
            options.EnablePersistence.Should().BeFalse();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NotificationWorkerCount_ShouldAllowZeroAndNegativeValues(int workerCount)
        {
            // Arrange
            var options = new MediatorOptions();

            // Act
            options.NotificationWorkerCount = workerCount;

            // Assert
            options.NotificationWorkerCount.Should().Be(workerCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void MaxRetryAttempts_ShouldAllowZeroAndNegativeValues(int attempts)
        {
            // Arrange
            var options = new MediatorOptions();

            // Act
            options.MaxRetryAttempts = attempts;

            // Assert
            options.MaxRetryAttempts.Should().Be(attempts);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(0.5)]
        [InlineData(1)]
        [InlineData(10)]
        public void RetryDelayMultiplier_ShouldAllowValidValues(double multiplier)
        {
            // Arrange
            var options = new MediatorOptions();

            // Act
            options.RetryDelayMultiplier = multiplier;

            // Assert
            options.RetryDelayMultiplier.Should().Be(multiplier);
        }

        [Fact]
        public void ProcessingBatchSize_ShouldDefaultToReasonableValue()
        {
            // Arrange & Act
            var options = new MediatorOptions();

            // Assert
            options.ProcessingBatchSize.Should().BeGreaterThan(0);
            options.ProcessingBatchSize.Should().BeLessOrEqualTo(1000); // Reasonable upper bound
        }

        [Theory]
        [InlineData(1)]
        [InlineData(10)]
        [InlineData(1000)]
        [InlineData(5000)]
        public void ProcessingBatchSize_ShouldAllowCustomValues(int batchSize)
        {
            // Arrange
            var options = new MediatorOptions();

            // Act
            options.ProcessingBatchSize = batchSize;

            // Assert
            options.ProcessingBatchSize.Should().Be(batchSize);
        }

        [Fact]
        public void TimeSpanProperties_ShouldAcceptZeroValues()
        {
            // Arrange
            var options = new MediatorOptions();

            // Act
            options.InitialRetryDelay = TimeSpan.Zero;
            options.ProcessingInterval = TimeSpan.Zero;
            options.CleanupRetentionPeriod = TimeSpan.Zero;
            options.CleanupInterval = TimeSpan.Zero;

            // Assert
            options.InitialRetryDelay.Should().Be(TimeSpan.Zero);
            options.ProcessingInterval.Should().Be(TimeSpan.Zero);
            options.CleanupRetentionPeriod.Should().Be(TimeSpan.Zero);
            options.CleanupInterval.Should().Be(TimeSpan.Zero);
        }
    }
}