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

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object? Deserialize(string? serializedNotification, Type notificationType)
        {
            if (string.IsNullOrWhiteSpace(serializedNotification))
                return null;

            try
            {
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
                return JsonSerializer.Serialize(notification, notificationType, Options);
            }
            catch
            {
                return "{}";
            }
        }
    }
}