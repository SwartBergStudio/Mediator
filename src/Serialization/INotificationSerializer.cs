namespace Mediator.Serialization
{
    /// <summary>
    /// Interface for serializing and deserializing notifications.
    /// </summary>
    public interface INotificationSerializer
    {
        /// <summary>
        /// Serialize a notification to a string.
        /// </summary>
        string Serialize(object notification, Type notificationType);

        /// <summary>
        /// Deserialize a notification from a string.
        /// </summary>
        object? Deserialize(string serializedNotification, Type notificationType);
    }
}