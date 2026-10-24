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
            Assert.Contains(typeof(TestNotification).AssemblyQualifiedName!, content);
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
            Assert.Equal(1, updatedItem!.AttemptCount);
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

        [Fact]
        public async Task FailAsync_WhenTheFileCannotBeAccessed_ShouldKeepTheNotificationAndReportTheError()
        {
            // An IO error while recording the retry (another process holding the file, such as a virus scanner) used to
            // delete the notification and report success, so it was never handled again.
            var notification = new TestNotification { Message = "Test message" };
            var id = await _persistence.PersistAsync(new NotificationWorkItem(notification, typeof(TestNotification), DateTime.UtcNow,
                _serializer.Serialize(notification, typeof(TestNotification))));
            var filePath = Path.Combine(_testDirectory, $"{id}.json");
            var before = await File.ReadAllTextAsync(filePath);

            using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Delete))
            {
                await _persistence.Invoking(p => p.FailAsync(id, new InvalidOperationException("handler failed"), DateTime.UtcNow.AddMinutes(1)))
                    .Should().ThrowAsync<IOException>();
            }

            File.Exists(filePath).Should().BeTrue("the notification must still be handled later");
            (await File.ReadAllTextAsync(filePath)).Should().Be(before);
            (await _persistence.GetPendingAsync()).Should().ContainSingle(p => p.Id == id);
        }

        [Fact]
        public async Task CleanupAsync_ShouldRemoveOldTemporaryFilesLeftByAnInterruptedWrite()
        {
            var orphan = Path.Combine(_testDirectory, "abc.json.tmp");
            await File.WriteAllTextAsync(orphan, "{\"id\":");
            File.SetCreationTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));

            await _persistence.CleanupAsync(DateTime.UtcNow.AddDays(-1));

            File.Exists(orphan).Should().BeFalse("only *.json was cleaned, so these accumulated forever");
        }

        [Theory]
        [InlineData("{\"id\":\"bad\",\"workItem\":\"not an object\"}")]
        [InlineData("{\"id\":\"bad\",\"createdAt\":null,\"workItem\":{\"assemblyQualifiedName\":\"System.String\",\"serializedNotification\":\"x\",\"createdAt\":null}}")]
        [InlineData("{\"id\":\"bad\",\"attemptCount\":1e30,\"workItem\":{\"assemblyQualifiedName\":\"System.String\",\"serializedNotification\":\"x\"}}")]
        public async Task GetPendingAsync_WithAFileOfTheWrongShape_ShouldStillReturnTheOtherItems(string content)
        {
            // Valid JSON of the wrong shape used to throw out of GetPendingAsync, so one bad file stopped recovery of
            // every other notification on every pass.
            var notification = new TestNotification { Message = "Test message" };
            var id = await _persistence.PersistAsync(new NotificationWorkItem(notification, typeof(TestNotification), DateTime.UtcNow,
                _serializer.Serialize(notification, typeof(TestNotification))));
            await File.WriteAllTextAsync(Path.Combine(_testDirectory, "bad.json"), content);

            var pending = (await _persistence.GetPendingAsync()).ToList();

            pending.Should().ContainSingle().Which.Id.Should().Be(id);
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