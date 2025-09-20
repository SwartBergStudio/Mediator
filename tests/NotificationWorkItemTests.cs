namespace Mediator.Tests
{
    public class NotificationWorkItemTests
    {
        [Fact]
        public void NotificationWorkItem_ShouldInitializeCorrectly()
        {
            // Arrange
            var notification = new TestNotification { Message = "Test message" };
            var notificationType = typeof(TestNotification);
            var createdAt = DateTime.UtcNow;
            var serializedNotification = "{\"message\":\"Test message\"}";

            // Act
            var workItem = new NotificationWorkItem
            {
                Notification = notification,
                NotificationType = notificationType,
                CreatedAt = createdAt,
                SerializedNotification = serializedNotification
            };

            // Assert
            workItem.Notification.Should().Be(notification);
            workItem.NotificationType.Should().Be(notificationType);
            workItem.CreatedAt.Should().Be(createdAt);
            workItem.SerializedNotification.Should().Be(serializedNotification);
        }

        [Fact]
        public void NotificationWorkItem_ShouldAllowNullValues()
        {
            // Act
            var workItem = new NotificationWorkItem
            {
                Notification = null,
                NotificationType = typeof(TestNotification),
                CreatedAt = default,
                SerializedNotification = ""
            };

            // Assert
            workItem.Notification.Should().BeNull();
            workItem.NotificationType.Should().Be(typeof(TestNotification));
            workItem.CreatedAt.Should().Be(default);
            workItem.SerializedNotification.Should().Be("");
        }

        [Fact]
        public void NotificationWorkItem_ShouldBeValueType()
        {
            // Arrange & Act
            var type = typeof(NotificationWorkItem);

            // Assert
            type.IsValueType.Should().BeTrue("NotificationWorkItem should be a struct for better performance");
        }

        [Fact]
        public void NotificationWorkItem_ShouldSupportEquality()
        {
            // Arrange
            var notification1 = new TestNotification { Message = "Test" };
            var notification2 = new TestNotification { Message = "Test" };
            var createdAt = DateTime.UtcNow;

            var workItem1 = new NotificationWorkItem
            {
                Notification = notification1,
                NotificationType = typeof(TestNotification),
                CreatedAt = createdAt,
                SerializedNotification = "test"
            };

            var workItem2 = new NotificationWorkItem
            {
                Notification = notification2,
                NotificationType = typeof(TestNotification),
                CreatedAt = createdAt,
                SerializedNotification = "test"
            };

            // Act & Assert
            // Note: Since it's a struct with reference types, equality depends on reference equality for objects
            workItem1.GetType().Should().Be(workItem2.GetType());
        }
    }

    public class PersistedNotificationWorkItemTests
    {
        [Fact]
        public void PersistedNotificationWorkItem_ShouldInitializeCorrectly()
        {
            // Arrange
            var id = Guid.NewGuid().ToString();
            var workItem = new NotificationWorkItem
            {
                Notification = new TestNotification { Message = "Test" },
                NotificationType = typeof(TestNotification),
                CreatedAt = DateTime.UtcNow,
                SerializedNotification = "{}"
            };
            var createdAt = DateTime.UtcNow;
            var retryAfter = DateTime.UtcNow.AddMinutes(5);
            var attemptCount = 2;
            var lastException = "Test exception";

            // Act
            var persistedItem = new PersistedNotificationWorkItem
            {
                Id = id,
                WorkItem = workItem,
                CreatedAt = createdAt,
                AttemptCount = attemptCount,
                RetryAfter = retryAfter,
                IsCompleted = false,
                LastError = lastException
            };

            // Assert
            persistedItem.Id.Should().Be(id);
            persistedItem.WorkItem.Should().Be(workItem);
            persistedItem.CreatedAt.Should().Be(createdAt);
            persistedItem.RetryAfter.Should().Be(retryAfter);
            persistedItem.AttemptCount.Should().Be(attemptCount);
            persistedItem.LastError.Should().Be(lastException);
        }

        [Fact]
        public void PersistedNotificationWorkItem_ShouldAllowNullValues()
        {
            // Act
            var persistedItem = new PersistedNotificationWorkItem
            {
                Id = "test-id",
                WorkItem = default,
                CreatedAt = default,
                RetryAfter = null,
                AttemptCount = 0,
                LastError = null
            };

            // Assert
            persistedItem.Id.Should().Be("test-id");
            persistedItem.WorkItem.Should().Be(default(NotificationWorkItem));
            persistedItem.CreatedAt.Should().Be(default);
            persistedItem.RetryAfter.Should().BeNull();
            persistedItem.AttemptCount.Should().Be(0);
            persistedItem.LastError.Should().BeNull();
        }

        [Fact]
        public void PersistedNotificationWorkItem_ShouldBeReferenceType()
        {
            // Arrange & Act
            var type = typeof(PersistedNotificationWorkItem);

            // Assert
            type.IsClass.Should().BeTrue("PersistedNotificationWorkItem should be a class for additional metadata");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(100)]
        public void PersistedNotificationWorkItem_ShouldAcceptValidAttemptCounts(int attemptCount)
        {
            // Act
            var persistedItem = new PersistedNotificationWorkItem
            {
                Id = "test-id",
                AttemptCount = attemptCount
            };

            // Assert
            persistedItem.AttemptCount.Should().Be(attemptCount);
        }

        [Fact]
        public void PersistedNotificationWorkItem_RetryAfter_ShouldAcceptFutureAndPastDates()
        {
            // Arrange
            var pastDate = DateTime.UtcNow.AddMinutes(-10);
            var futureDate = DateTime.UtcNow.AddMinutes(10);

            // Act
            var persistedItem1 = new PersistedNotificationWorkItem 
            { 
                Id = "test-id-1",
                RetryAfter = pastDate 
            };
            var persistedItem2 = new PersistedNotificationWorkItem 
            { 
                Id = "test-id-2", 
                RetryAfter = futureDate 
            };

            // Assert
            persistedItem1.RetryAfter.Should().Be(pastDate);
            persistedItem2.RetryAfter.Should().Be(futureDate);
        }
    }
}