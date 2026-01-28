using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Mediator.Serialization
{
    /// <summary>
    /// JSON-based implementation of notification serializer.
    /// </summary>
    public class JsonNotificationSerializer : INotificationSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultBufferSize = 2048
        };

        private class TypeWrappedNotification
        {
            public string Type { get; set; } = string.Empty;
            public JsonElement Data { get; set; }
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object? Deserialize(string? serializedNotification, Type notificationType)
        {
            if (string.IsNullOrWhiteSpace(serializedNotification))
                return null;

            try
            {
                // Try to detect if this is a wrapped notification
                using var doc = JsonDocument.Parse(serializedNotification);
                if (doc.RootElement.TryGetProperty("type", out var typeProp) &&
                    doc.RootElement.TryGetProperty("data", out var dataProp))
                {
                    var typeName = typeProp.GetString();
                    if (!string.IsNullOrWhiteSpace(typeName))
                    {
                        var type = Type.GetType(typeName, throwOnError: false);
                        if (type != null && notificationType.IsAssignableFrom(type))
                        {
                            return JsonSerializer.Deserialize(dataProp.GetRawText(), type, Options);
                        }
                    }
                }
                // Fallback: try to deserialize as the requested type
                return JsonSerializer.Deserialize(serializedNotification, notificationType, Options);
            }
            catch
            {
                return null;
            }
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string Serialize(object? notification, Type notificationType)
        {
            if (notification is null)
                return "null";

            try
            {
                var concreteType = notification.GetType();
                var dataJson = JsonSerializer.SerializeToElement(notification, concreteType, Options);
                var wrapper = new TypeWrappedNotification
                {
                    Type = concreteType.AssemblyQualifiedName!,
                    Data = dataJson
                };
                return JsonSerializer.Serialize(wrapper, Options);
            }
            catch
            {
                return "{}";
            }
        }
    }
}