using System.Text.Json;

namespace Mediator.Tests
{
    /// <summary>
    /// The serializer and file persistence were rewritten to avoid reflection-based JSON (for Native AOT).
    /// These tests pin the output to the exact format produced by the previous reflection-based implementation,
    /// so payloads and files written before an upgrade stay readable.
    /// </summary>
    public class PersistenceFormatCompatibilityTests : IDisposable
    {
        private static readonly JsonSerializerOptions LegacyOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mediator-format-tests", Guid.NewGuid().ToString());

        [Fact]
        public void Serialize_ShouldMatchLegacyWrappedFormat()
        {
            var notification = new ComplexTestNotification { Id = Guid.NewGuid(), Message = "café <&> \"q\"", CreatedAt = DateTime.UtcNow, Tags = new[] { "a", "b" }, Metadata = { ["n"] = 1 } };

            var legacy = JsonSerializer.Serialize(new
            {
                Type = typeof(ComplexTestNotification).AssemblyQualifiedName,
                Data = JsonSerializer.SerializeToElement(notification, typeof(ComplexTestNotification), LegacyOptions)
            }, LegacyOptions);

            new JsonNotificationSerializer().Serialize(notification, typeof(ComplexTestNotification)).Should().Be(legacy);
        }

        [Fact]
        public async Task PersistAsync_ShouldWriteLegacyFileFormat()
        {
            using var persistence = new FileNotificationPersistence(_directory);
            var createdAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var workItem = new NotificationWorkItem(null, typeof(TestNotification), createdAt, "{\"message\":\"x\"}");

            var id = await persistence.PersistAsync(workItem);
            var written = await File.ReadAllTextAsync(Path.Combine(_directory, id + ".json"));

            using var document = JsonDocument.Parse(written);
            var persistedCreatedAt = document.RootElement.GetProperty("createdAt").GetDateTime();
            var legacy = JsonSerializer.Serialize(new
            {
                Id = id,
                CreatedAt = persistedCreatedAt,
                RetryAfter = (DateTime?)null,
                AttemptCount = 0,
                WorkItem = new
                {
                    AssemblyQualifiedName = typeof(TestNotification).AssemblyQualifiedName,
                    SerializedNotification = workItem.SerializedNotification,
                    CreatedAt = createdAt
                },
                LastException = (string?)null
            }, LegacyOptions);

            written.Should().Be(legacy);
        }

        [Fact]
        public async Task GetPendingAsync_ShouldReadFileWrittenByLegacyVersion()
        {
            Directory.CreateDirectory(_directory);
            var legacy = JsonSerializer.Serialize(new
            {
                Id = "legacy1",
                CreatedAt = DateTime.UtcNow,
                RetryAfter = DateTime.UtcNow.AddMinutes(-1),
                AttemptCount = 2,
                WorkItem = new
                {
                    AssemblyQualifiedName = typeof(TestNotification).AssemblyQualifiedName,
                    SerializedNotification = "{\"message\":\"old\"}",
                    CreatedAt = DateTime.UtcNow
                },
                LastException = "boom"
            }, LegacyOptions);
            await File.WriteAllTextAsync(Path.Combine(_directory, "legacy1.json"), legacy);

            using var persistence = new FileNotificationPersistence(_directory);
            var pending = (await persistence.GetPendingAsync()).ToList();

            pending.Should().ContainSingle();
            pending[0].Id.Should().Be("legacy1");
            pending[0].AttemptCount.Should().Be(2);
            pending[0].WorkItem.NotificationType.Should().Be(typeof(TestNotification));
            pending[0].WorkItem.SerializedNotification.Should().Be("{\"message\":\"old\"}");
        }

        [Fact]
        public async Task GetPendingAsync_FilesWaitingForRetry_ShouldNotStarveReadyFiles()
        {
            using var persistence = new FileNotificationPersistence(_directory);
            for (var i = 0; i < 5; i++)
            {
                var id = await persistence.PersistAsync(new NotificationWorkItem(null, typeof(TestNotification), DateTime.UtcNow, "{}"));
                await persistence.FailAsync(id, new Exception(), DateTime.UtcNow.AddHours(1));
            }
            var readyId = await persistence.PersistAsync(new NotificationWorkItem(null, typeof(TestNotification), DateTime.UtcNow, "{}"));

            var pending = await persistence.GetPendingAsync(batchSize: 1);

            pending.Should().ContainSingle(p => p.Id == readyId);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
            catch (IOException) { }
        }
    }
}
