using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Mediator.Serialization
{
    /// <summary>
    /// JSON-based implementation of notification serializer.
    /// </summary>
    /// <remarks>
    /// The payload is written as <c>{"type":"&lt;assembly qualified name&gt;","data":{...}}</c> so derived notifications
    /// published as a base type round-trip as their concrete type.
    /// <para>
    /// The parameterless constructor uses reflection-based System.Text.Json. For trimmed or Native AOT apps use
    /// <see cref="JsonNotificationSerializer(JsonSerializerOptions)"/> with options from a source-generated
    /// <see cref="JsonSerializerContext"/> that includes every persisted notification type.
    /// </para>
    /// </remarks>
    public class JsonNotificationSerializer : INotificationSerializer
    {
        private const string TypePropertyName = "type";
        private const string DataPropertyName = "data";

        private readonly JsonSerializerOptions _options;

        /// <summary>
        /// Creates a serializer that uses reflection-based System.Text.Json with camelCase property names.
        /// </summary>
        [RequiresUnreferencedCode("Reflection-based JSON serialization may require types that are trimmed. Use the JsonSerializerOptions constructor with a JsonSerializerContext for trimmed apps.")]
        [RequiresDynamicCode("Reflection-based JSON serialization requires runtime code generation. Use the JsonSerializerOptions constructor with a JsonSerializerContext for Native AOT apps.")]
        public JsonNotificationSerializer()
            : this(new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultBufferSize = 2048,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            })
        {
        }

        /// <summary>
        /// Creates a serializer that uses the given options. This constructor is trimming and Native AOT safe when
        /// <see cref="JsonSerializerOptions.TypeInfoResolver"/> is a source-generated <see cref="JsonSerializerContext"/>.
        /// </summary>
        /// <param name="options">Serializer options. Must have a <see cref="JsonSerializerOptions.TypeInfoResolver"/>.</param>
        public JsonNotificationSerializer(JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.TypeInfoResolver is null)
                throw new ArgumentException("JsonSerializerOptions.TypeInfoResolver must be set, for example to a source-generated JsonSerializerContext.", nameof(options));

            _options = options;
        }

        /// <inheritdoc />
        public object? Deserialize(string? serializedNotification, Type notificationType)
        {
            if (string.IsNullOrWhiteSpace(serializedNotification))
                return null;

            try
            {
                using var document = JsonDocument.Parse(serializedNotification);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty(TypePropertyName, out var typeProperty) &&
                    root.TryGetProperty(DataPropertyName, out var dataProperty) &&
                    typeProperty.ValueKind == JsonValueKind.String)
                {
                    var concreteType = NotificationTypeResolver.Resolve(typeProperty.GetString());
                    if (concreteType != null && notificationType.IsAssignableFrom(concreteType))
                    {
                        return dataProperty.Deserialize(_options.GetTypeInfo(concreteType));
                    }
                }

                // Not a wrapped payload: deserialize as the requested type.
                return root.Deserialize(_options.GetTypeInfo(notificationType));
            }
            catch
            {
                return null;
            }
        }

        /// <inheritdoc />
        public string Serialize(object? notification, Type notificationType)
        {
            if (notification is null)
                return "null";

            try
            {
                var concreteType = notification.GetType();
                var buffer = new ArrayBufferWriter<byte>(_options.DefaultBufferSize);
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString(TypePropertyName, concreteType.AssemblyQualifiedName);
                    writer.WritePropertyName(DataPropertyName);
                    JsonSerializer.Serialize(writer, notification, _options.GetTypeInfo(concreteType));
                    writer.WriteEndObject();
                }
                return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
            }
            catch
            {
                return "{}";
            }
        }
    }
}
