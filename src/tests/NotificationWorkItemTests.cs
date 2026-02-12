namespace Mediator.Tests
{
    public class NotificationWorkItemTests
    {
        [Fact]
        public void NotificationWorkItem_ShouldBeValueType()
        {
            typeof(NotificationWorkItem).IsValueType.Should().BeTrue("NotificationWorkItem should be a struct for better performance");
        }
    }

    public class PersistedNotificationWorkItemTests
    {
        [Fact]
        public void PersistedNotificationWorkItem_ShouldBeReferenceType()
        {
            typeof(Persistence.PersistedNotificationWorkItem).IsClass.Should().BeTrue("PersistedNotificationWorkItem should be a class for additional metadata");
        }
    }
}
