using System.Text.Json;

namespace Mediator.Tests
{
    public class JsonNotificationSerializerTests
    {
        private readonly JsonNotificationSerializer _serializer;

        public JsonNotificationSerializerTests()
        {
            _serializer = new JsonNotificationSerializer();
        }

        [Fact]
        public void Serialize_WithValidNotification_ShouldReturnJsonString()
        {
            // Arrange
            var notification = new TestNotification 
            { 
                Message = "Hello World",
                CreatedAt = new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc)
            };

            // Act
            var result = _serializer.Serialize(notification, typeof(TestNotification));

            // Assert
            Assert.NotNull(result);
            Assert.NotEqual("", result);
            Assert.NotEqual("{}", result);
            
            // Should be valid JSON containing our message
            Assert.True(result.Contains("Hello World") || result.Contains("hello world"), 
                $"Expected JSON to contain 'Hello World' but got: {result}");
            
            // Verify it's valid JSON by deserializing with custom options
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var deserialized = JsonSerializer.Deserialize<TestNotification>(result, options);
            Assert.NotNull(deserialized);
            Assert.Equal(notification.Message, deserialized.Message);
        }

        [Fact]
        public void Deserialize_WithValidJsonString_ShouldReturnNotification()
        {
            // Arrange
            var originalNotification = new TestNotification 
            { 
                Message = "Hello World",
                CreatedAt = new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc)
            };
            var json = _serializer.Serialize(originalNotification, typeof(TestNotification));

            // Act
            var result = _serializer.Deserialize(json, typeof(TestNotification)) as TestNotification;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(originalNotification.Message, result.Message);
            Assert.Equal(originalNotification.CreatedAt, result.CreatedAt);
        }

        [Fact]
        public void Deserialize_WithNullOrEmptyString_ShouldReturnNull()
        {
            // Act & Assert
            Assert.Null(_serializer.Deserialize(null, typeof(TestNotification)));
            Assert.Null(_serializer.Deserialize(string.Empty, typeof(TestNotification)));
            Assert.Null(_serializer.Deserialize("   ", typeof(TestNotification)));
        }

        [Fact]
        public void Serialize_Deserialize_RoundTrip_ShouldPreserveData()
        {
            // Arrange
            var originalNotification = new ComplexTestNotification
            {
                Id = Guid.NewGuid(),
                Message = "Complex message with special chars",
                CreatedAt = DateTime.UtcNow,
                Tags = new[] { "tag1", "tag2", "tag3" },
                Metadata = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "key1", "value1" },
                    { "key2", 42 },
                    { "key3", true }
                }
            };

            // Act
            var serialized = _serializer.Serialize(originalNotification, typeof(ComplexTestNotification));
            var deserialized = _serializer.Deserialize(serialized, typeof(ComplexTestNotification)) as ComplexTestNotification;

            // Assert
            Assert.NotNull(deserialized);
            Assert.Equal(originalNotification.Id, deserialized.Id);
            Assert.Equal(originalNotification.Message, deserialized.Message);
            Assert.Equal(originalNotification.Tags.Length, deserialized.Tags.Length);
        }

        [Fact]
        public void Serialize_WithNullNotification_ShouldReturnJsonNull()
        {
            // Act
            var result = _serializer.Serialize(null, typeof(TestNotification));

            // Assert
            Assert.Equal("null", result); // JSON null, not C# null
        }

        [Fact]
        public void Deserialize_WithInvalidJson_ShouldReturnNull()
        {
            // Arrange
            var invalidJson = "{ invalid json }";

            // Act
            var result = _serializer.Deserialize(invalidJson, typeof(TestNotification));

            // Assert
            Assert.Null(result); // Implementation catches exceptions and returns null
        }
    }

    // Additional test notification type for complex scenarios
    public class ComplexTestNotification : INotification
    {
        public Guid Id { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
        public System.Collections.Generic.Dictionary<string, object> Metadata { get; set; } = new();
    }
}