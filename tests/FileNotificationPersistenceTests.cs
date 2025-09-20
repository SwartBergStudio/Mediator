namespace Mediator.Tests
{
    public class FileNotificationPersistenceTests : IDisposable
    {
        private readonly string _testDirectory;
        private readonly FileNotificationPersistence _persistence;
        private readonly JsonNotificationSerializer _serializer;

        public FileNotificationPersistenceTests()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "mediator-tests", Guid.NewGuid().ToString());
            _persistence = new FileNotificationPersistence(_testDirectory);
            _serializer = new JsonNotificationSerializer();
        }

        [Fact]
        public async Task PersistAsync_ShouldCreateFileWithCorrectContent()
        {
            // Arrange
            var notification = new TestNotification { Message = "Test message" };
            var workItem = new NotificationWorkItem
            {
                Notification = notification,
                NotificationType = typeof(TestNotification),
                CreatedAt = DateTime.UtcNow,
                SerializedNotification = _serializer.Serialize(notification, typeof(TestNotification))
            };

            // Act
            var id = await _persistence.PersistAsync(workItem);

            // Assert
            Assert.NotNull(id);
            Assert.NotEmpty(id);
            
            var filePath = Path.Combine(_testDirectory, $"{id}.json");
            Assert.True(File.Exists(filePath));

            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("Test message", content);
            Assert.Contains(typeof(TestNotification).AssemblyQualifiedName, content);
        }

        [Fact]
        public async Task GetPendingAsync_ShouldReturnPersistedItems()
        {
            // Arrange
            var notification = new TestNotification { Message = "Test message" };
            var workItem = new NotificationWorkItem
            {
                Notification = notification,
                NotificationType = typeof(TestNotification),
                CreatedAt = DateTime.UtcNow,
                SerializedNotification = _serializer.Serialize(notification, typeof(TestNotification))
            };

            var id = await _persistence.PersistAsync(workItem);

            // Act
            var pendingItems = await _persistence.GetPendingAsync();

            // Assert
            var pendingList = pendingItems.ToList();
            Assert.Single(pendingList);
            Assert.Equal(id, pendingList[0].Id);
            Assert.Equal(typeof(TestNotification), pendingList[0].WorkItem.NotificationType);
        }

        [Fact]
        public async Task CompleteAsync_ShouldRemoveFile()
        {
            // Arrange
            var notification = new TestNotification { Message = "Test message" };
            var workItem = new NotificationWorkItem
            {
                Notification = notification,
                NotificationType = typeof(TestNotification),
                CreatedAt = DateTime.UtcNow,
                SerializedNotification = _serializer.Serialize(notification, typeof(TestNotification))
            };

            var id = await _persistence.PersistAsync(workItem);
            var filePath = Path.Combine(_testDirectory, $"{id}.json");
            Assert.True(File.Exists(filePath));

            // Act
            await _persistence.CompleteAsync(id);

            // Assert
            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task FailAsync_ShouldUpdateFileWithErrorInfo()
        {
            // Arrange
            var notification = new TestNotification { Message = "Test message" };
            var workItem = new NotificationWorkItem
            {
                Notification = notification,
                NotificationType = typeof(TestNotification),
                CreatedAt = DateTime.UtcNow,
                SerializedNotification = _serializer.Serialize(notification, typeof(TestNotification))
            };

            var id = await _persistence.PersistAsync(workItem);
            var exception = new InvalidOperationException("Test error");

            // Act
            await _persistence.FailAsync(id, exception, null);

            // Assert
            // Wait a moment to ensure file write completes
            await Task.Delay(50);
            
            var pendingItems = await _persistence.GetPendingAsync();
            var updatedItem = pendingItems.FirstOrDefault(x => x.Id == id);
            
            Assert.NotNull(updatedItem);
            Assert.Equal(1, updatedItem.AttemptCount);
        }

        [Fact]
        public async Task GetPendingAsync_WithRetryAfter_ShouldNotReturnItemsNotReadyForRetry()
        {
            // Arrange
            var notification = new TestNotification { Message = "Test message" };
            var workItem = new NotificationWorkItem
            {
                Notification = notification,
                NotificationType = typeof(TestNotification),
                CreatedAt = DateTime.UtcNow,
                SerializedNotification = _serializer.Serialize(notification, typeof(TestNotification))
            };

            var id = await _persistence.PersistAsync(workItem);
            var exception = new InvalidOperationException("Test error");
            var retryAfter = DateTime.UtcNow.AddMinutes(5); // Far in the future

            await _persistence.FailAsync(id, exception, retryAfter);

            // Act
            var pendingItems = await _persistence.GetPendingAsync();

            // Assert
            Assert.Empty(pendingItems);
        }

        [Fact]
        public async Task CleanupAsync_ShouldRemoveOldFiles()
        {
            // Arrange
            var notification = new TestNotification { Message = "Test message" };
            var workItem = new NotificationWorkItem
            {
                Notification = notification,
                NotificationType = typeof(TestNotification),
                CreatedAt = DateTime.UtcNow,
                SerializedNotification = _serializer.Serialize(notification, typeof(TestNotification))
            };

            var id = await _persistence.PersistAsync(workItem);
            var filePath = Path.Combine(_testDirectory, $"{id}.json");

            // Make the file appear old by changing its creation time
            File.SetCreationTimeUtc(filePath, DateTime.UtcNow.AddDays(-2));

            // Act
            await _persistence.CleanupAsync(DateTime.UtcNow.AddDays(-1));

            // Assert
            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task GetPendingAsync_WithBatchSize_ShouldRespectLimit()
        {
            // Arrange
            var notification = new TestNotification { Message = "Test message" };
            var tasks = new List<Task<string>>();

            // Create 5 work items
            for (int i = 0; i < 5; i++)
            {
                var workItem = new NotificationWorkItem
                {
                    Notification = notification,
                    NotificationType = typeof(TestNotification),
                    CreatedAt = DateTime.UtcNow,
                    SerializedNotification = _serializer.Serialize(notification, typeof(TestNotification))
                };
                tasks.Add(_persistence.PersistAsync(workItem));
            }

            await Task.WhenAll(tasks);

            // Act
            var pendingItems = await _persistence.GetPendingAsync(batchSize: 3);

            // Assert
            Assert.True(pendingItems.Count() <= 3);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
    }
}