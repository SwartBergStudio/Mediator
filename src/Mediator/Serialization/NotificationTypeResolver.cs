using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Mediator.Serialization;

/// <summary>
/// Resolves persisted notification type names back to <see cref="Type"/> instances.
/// </summary>
/// <remarks>
/// Notification types registered through <see cref="MediatorRegistry.RegisterNotification{TNotification}"/>
/// (which the source generator does for every handled notification) are resolved from a lookup table, which is
/// trimming and Native AOT safe. Other names fall back to <see cref="Type.GetType(string, bool)"/>.
/// </remarks>
internal static class NotificationTypeResolver
{
    private static readonly ConcurrentDictionary<string, Type> s_knownTypes = new(StringComparer.Ordinal);

    public static void Register(Type type)
    {
        if (type.AssemblyQualifiedName is { } name)
            s_knownTypes[name] = type;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2057:Unrecognized value passed to Type.GetType",
        Justification = "Fallback for notification types that were not registered. Registered types (all types handled via the source generator) never reach this path.")]
    public static Type? Resolve(string? assemblyQualifiedName)
    {
        if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
            return null;

        return s_knownTypes.TryGetValue(assemblyQualifiedName, out var type)
            ? type
            : Type.GetType(assemblyQualifiedName, throwOnError: false);
    }
}
