using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Mediator.Core.Wrappers;

/// <summary>
/// Process-wide cache of strongly-typed handler wrappers keyed by the runtime message type.
/// Wrappers are stateless and fully determined by their generic arguments, so a single static cache is shared
/// by every mediator instance and every DI scope.
/// </summary>
/// <remarks>
/// Wrappers are either registered up-front (source generator / <see cref="MediatorRegistry"/>), which is
/// trimming and Native AOT safe, or created lazily via <c>MakeGenericType</c> when dynamic code is available.
/// </remarks>
internal static class HandlerWrapperCache
{
    private const string AotGuidance =
        "Register handlers with the SwartBerg.Mediator.SourceGenerator package (services.AddMediatorHandlers()) " +
        "or call MediatorRegistry.Register* for this type when running with Native AOT.";

    private static readonly ConcurrentDictionary<Type, CommandHandlerWrapper> s_commandWrappers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper?> s_notificationWrappers = new();

    public static RequestHandlerWrapper<TResponse> GetRequestWrapper<TResponse>(Type requestType)
    {
        var cache = RequestCache<TResponse>.Wrappers;
        if (cache.TryGetValue(requestType, out var wrapper))
            return wrapper;

        return cache.GetOrAdd(requestType, static t =>
            CreateWrapper<RequestHandlerWrapper<TResponse>>(typeof(RequestHandlerWrapper<,>), t, typeof(TResponse))
            ?? throw MissingWrapper(t));
    }

    public static CommandHandlerWrapper GetCommandWrapper(Type requestType)
    {
        if (s_commandWrappers.TryGetValue(requestType, out var wrapper))
            return wrapper;

        return s_commandWrappers.GetOrAdd(requestType, static t =>
            CreateWrapper<CommandHandlerWrapper>(typeof(CommandHandlerWrapper<>), t)
            ?? throw MissingWrapper(t));
    }

    /// <summary>
    /// Returns the wrapper for a notification type, or <c>null</c> when no wrapper is registered and one cannot be
    /// created (Native AOT). A notification without a wrapper has no generated handlers, so it is treated as unhandled.
    /// </summary>
    public static NotificationHandlerWrapper? GetNotificationWrapper(Type notificationType)
    {
        if (s_notificationWrappers.TryGetValue(notificationType, out var wrapper))
            return wrapper;

        return s_notificationWrappers.GetOrAdd(notificationType, static t =>
            CreateWrapper<NotificationHandlerWrapper>(typeof(NotificationHandlerWrapper<>), t));
    }

    public static StreamRequestHandlerWrapper<TResponse> GetStreamWrapper<TResponse>(Type requestType)
    {
        var cache = StreamCache<TResponse>.Wrappers;
        if (cache.TryGetValue(requestType, out var wrapper))
            return wrapper;

        return cache.GetOrAdd(requestType, static t =>
            CreateWrapper<StreamRequestHandlerWrapper<TResponse>>(typeof(StreamRequestHandlerWrapper<,>), t, typeof(TResponse))
            ?? throw MissingWrapper(t));
    }

    public static void RegisterRequest<TRequest, TResponse>() where TRequest : IRequest<TResponse>
        => RequestCache<TResponse>.Wrappers[typeof(TRequest)] = new RequestHandlerWrapper<TRequest, TResponse>();

    public static void RegisterCommand<TRequest>() where TRequest : IRequest
        => s_commandWrappers[typeof(TRequest)] = new CommandHandlerWrapper<TRequest>();

    public static void RegisterNotification<TNotification>() where TNotification : INotification
        => s_notificationWrappers[typeof(TNotification)] = new NotificationHandlerWrapper<TNotification>();

    public static void RegisterStreamRequest<TRequest, TResponse>() where TRequest : IStreamRequest<TResponse>
        => StreamCache<TResponse>.Wrappers[typeof(TRequest)] = new StreamRequestHandlerWrapper<TRequest, TResponse>();

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Only reached when RuntimeFeature.IsDynamicCodeSupported is true. Native AOT apps register wrappers up-front.")]
    [UnconditionalSuppressMessage("Trimming", "IL2055:MakeGenericType",
        Justification = "Wrapper types have no member requirements on their generic arguments; their constructors are preserved via DynamicDependency.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers",
        Justification = "Wrapper constructors are preserved via DynamicDependency.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
        Justification = "Wrapper constructors are preserved via DynamicDependency.")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(RequestHandlerWrapper<,>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(CommandHandlerWrapper<>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(NotificationHandlerWrapper<>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(StreamRequestHandlerWrapper<,>))]
    private static T? CreateWrapper<T>(Type openWrapperType, params Type[] typeArguments) where T : class
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return null;

        return (T)Activator.CreateInstance(openWrapperType.MakeGenericType(typeArguments))!;
    }

    private static InvalidOperationException MissingWrapper(Type messageType)
        => new($"No handler wrapper is registered for '{messageType.FullName}'. {AotGuidance}");

    private static class RequestCache<TResponse>
    {
        public static readonly ConcurrentDictionary<Type, RequestHandlerWrapper<TResponse>> Wrappers = new();
    }

    private static class StreamCache<TResponse>
    {
        public static readonly ConcurrentDictionary<Type, StreamRequestHandlerWrapper<TResponse>> Wrappers = new();
    }
}
