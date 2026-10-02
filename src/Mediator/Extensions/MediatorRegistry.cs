using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Mediator.Core.Wrappers;

namespace Mediator;

/// <summary>
/// Reflection-free registration API used by the SwartBerg.Mediator source generator.
/// </summary>
/// <remarks>
/// Each <c>Register*</c> method pre-creates the strongly-typed dispatcher for a message type so that
/// <see cref="IMediator"/> never needs <c>MakeGenericType</c> at runtime. This is what makes the mediator
/// trimming and Native AOT compatible. Calling these methods manually is supported for apps that do not use
/// the generator; calling them more than once is harmless.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public static class MediatorRegistry
{
    /// <summary>Pre-registers the dispatcher for a request that returns <typeparamref name="TResponse"/>.</summary>
    public static void RegisterRequest<TRequest, TResponse>() where TRequest : IRequest<TResponse>
        => HandlerWrapperCache.RegisterRequest<TRequest, TResponse>();

    /// <summary>Pre-registers the dispatcher for a command (request without a response).</summary>
    public static void RegisterCommand<TRequest>() where TRequest : IRequest
        => HandlerWrapperCache.RegisterCommand<TRequest>();

    /// <summary>Pre-registers the dispatcher for a notification type.</summary>
    public static void RegisterNotification<TNotification>() where TNotification : INotification
        => HandlerWrapperCache.RegisterNotification<TNotification>();

    /// <summary>Pre-registers the dispatcher for a streaming request.</summary>
    public static void RegisterStreamRequest<TRequest, TResponse>() where TRequest : IStreamRequest<TResponse>
        => HandlerWrapperCache.RegisterStreamRequest<TRequest, TResponse>();

    /// <summary>
    /// Adds handler registrations, skipping any (service, implementation) pair that is already registered.
    /// Matches the de-duplication behaviour of assembly scanning so generated and reflection registrations can be mixed.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="descriptors">Handler descriptors to add.</param>
    public static IServiceCollection AddHandlers(IServiceCollection services, IEnumerable<ServiceDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptors);

        var existing = HandlerRegistrations.CollectExisting(services);
        foreach (var descriptor in descriptors)
        {
            if (descriptor.ImplementationType is null || existing.Add((descriptor.ServiceType, descriptor.ImplementationType)))
            {
                services.Add(descriptor);
            }
        }
        return services;
    }
}
