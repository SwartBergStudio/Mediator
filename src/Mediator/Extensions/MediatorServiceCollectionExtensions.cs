using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mediator;

/// <summary>
/// Extension methods for registering the Mediator infrastructure with dependency injection.
/// </summary>
public static class MediatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Mediator infrastructure and all handlers found in the provided assemblies.
    /// </summary>
    /// <remarks>
    /// Handlers are discovered with reflection. For trimmed or Native AOT apps use <see cref="AddMediatorCore"/>
    /// together with the source-generated <c>AddMediatorHandlers()</c> from the SwartBerg.Mediator.SourceGenerator package.
    /// </remarks>
    public static IServiceCollection AddMediator(this IServiceCollection services, params Assembly[] assemblies)
        => services.AddMediator(static _ => { }, assemblies);

    /// <summary>
    /// Registers the Mediator infrastructure with configuration and all handlers found in the provided assemblies.
    /// </summary>
    /// <remarks>
    /// Handlers are discovered with reflection. For trimmed or Native AOT apps use <see cref="AddMediatorCore"/>
    /// together with the source-generated <c>AddMediatorHandlers()</c> from the SwartBerg.Mediator.SourceGenerator package.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Kept unannotated for backward compatibility. Trimmed/AOT apps should use AddMediatorCore + the source generator; this is documented on the method.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Kept unannotated for backward compatibility. Trimmed/AOT apps should use AddMediatorCore + the source generator; this is documented on the method.")]
    public static IServiceCollection AddMediator(this IServiceCollection services,
        Action<MediatorOptions> configureOptions, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        services.AddMediatorCore(configureOptions);
        HandlerRegistrations.RegisterFromAssemblies(services, assemblies);
        return services;
    }

    /// <summary>
    /// Registers the Mediator infrastructure without scanning for handlers. This method is trimming and
    /// Native AOT safe; register handlers with the source-generated <c>AddMediatorHandlers()</c> or manually.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional options configuration.</param>
    public static IServiceCollection AddMediatorCore(this IServiceCollection services, Action<MediatorOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureOptions != null)
            services.Configure(configureOptions);

        services.TryAddSingleton<IScopeProvider, DefaultScopeProvider>();
        // The mediator and dispatchers hold no state of their own (dispatchers are cached per message type),
        // so they are transient: cheap to create per scope, and handlers still resolve from the caller's scope.
        services.TryAddTransient<IRequestDispatcher, RequestDispatcher>();
        services.TryAddTransient<ICommandDispatcher, CommandDispatcher>();
        services.TryAddSingleton<INotificationPublisher, NotificationPublisher>();
        // Starts the publisher with the host (recovering stored notifications) and drains it on shutdown.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, NotificationPublisherLifetime>());
        // Runs the handlers of notifications received from a broker (see AddNotificationTransport).
        services.TryAddSingleton<INotificationDispatcher, NotificationDispatcher>();
        services.TryAddTransient<IStreamRequestDispatcher, StreamRequestDispatcher>();
        services.TryAddTransient<IMediator, Core.Mediator>();

        // Persistence and serialization are only registered when explicitly enabled. The options delegate is
        // evaluated here so the flag is known at registration time.
        if (configureOptions != null)
        {
            var options = new MediatorOptions();
            configureOptions(options);
            if (options.EnablePersistence)
                AddDefaultPersistence(services);
        }

        return services;
    }

    /// <summary>
    /// Publishes notifications through a message broker instead of the in-process background queue: <c>Publish</c> hands
    /// each notification to <typeparamref name="TTransport"/>, and the broker's consumer runs the handlers through
    /// <see cref="INotificationDispatcher"/>. Works before or after <c>AddMediator</c>.
    /// </summary>
    /// <remarks>
    /// Notifications are serialized with the registered <see cref="INotificationSerializer"/>. When none is registered,
    /// <see cref="JsonNotificationSerializer"/> with reflection is used; Native AOT apps should register one built from a
    /// source-generated <c>JsonSerializerContext</c> first. <see cref="MediatorOptions.EnablePersistence"/> is ignored: the
    /// broker stores the notifications.
    /// </remarks>
    /// <typeparam name="TTransport">The transport, registered as a singleton.</typeparam>
    public static IServiceCollection AddNotificationTransport<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransport>(this IServiceCollection services)
        where TTransport : class, INotificationTransport
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<INotificationTransport>();
        services.AddSingleton<INotificationTransport, TTransport>();
        // Replaces the in-process publisher whether AddMediator ran before this or runs after it (it only TryAdds).
        services.RemoveAll<INotificationPublisher>();
        services.AddSingleton<INotificationPublisher, TransportNotificationPublisher>();
        AddDefaultSerializer(services);
        return services;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Only used when no serializer was registered. Native AOT apps should register a JsonNotificationSerializer built from a source-generated JsonSerializerContext before calling AddNotificationTransport.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Only used when no serializer was registered. Native AOT apps should register a JsonNotificationSerializer built from a source-generated JsonSerializerContext before calling AddNotificationTransport.")]
    private static void AddDefaultSerializer(IServiceCollection services)
        => services.TryAddSingleton<INotificationSerializer>(static _ => new JsonNotificationSerializer());

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Persistence is opt-in. Native AOT apps should register a JsonNotificationSerializer built from a source-generated JsonSerializerContext before calling AddMediator.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Persistence is opt-in. Native AOT apps should register a JsonNotificationSerializer built from a source-generated JsonSerializerContext before calling AddMediator.")]
    private static void AddDefaultPersistence(IServiceCollection services)
    {
        services.TryAddSingleton<INotificationPersistence>(static _ => new FileNotificationPersistence());
        services.TryAddSingleton<INotificationSerializer>(static _ => new JsonNotificationSerializer());
    }
}
