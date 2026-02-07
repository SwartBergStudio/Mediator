using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Mediator;

/// <summary>
/// Extension methods for registering the Mediator infrastructure with dependency injection.
/// </summary>
public static class MediatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Mediator infrastructure and all handlers found in the provided assemblies.
    /// </summary>
    public static IServiceCollection AddMediator(this IServiceCollection services, params Assembly[] assemblies)
    {
        return services.AddMediator(options => { }, assemblies);
    }

    /// <summary>
    /// Registers the Mediator infrastructure with configuration and all handlers found in the provided assemblies.
    /// </summary>
    public static IServiceCollection AddMediator(this IServiceCollection services,
        Action<MediatorOptions> configureOptions, params Assembly[] assemblies)
    {
        if (configureOptions != null)
            services.Configure(configureOptions);

        services.AddSingleton<IScopeProvider, Core.DefaultScopeProvider>();
        services.AddSingleton<IRequestDispatcher, Core.RequestDispatcher>();
        services.AddSingleton<ICommandDispatcher, Core.CommandDispatcher>();
        services.AddSingleton<INotificationPublisher, Core.NotificationPublisher>();
        services.AddSingleton<IMediator, Core.Mediator>();

        services.TryAddSingleton<INotificationPersistence, FileNotificationPersistence>();
        services.TryAddSingleton<INotificationSerializer, JsonNotificationSerializer>();

        RegisterHandlers(services, assemblies);

        return services;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RegisterHandlers(IServiceCollection services, Assembly[] assemblies)
    {
        if (assemblies.Length == 0) return;

        var registrations = new List<(Type service, Type implementation)>(256);
        var seenRegistrations = new HashSet<(Type, Type)>();

        foreach (var assembly in assemblies)
        {
            var types = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && !t.IsInterface)
                .ToArray();

            foreach (var type in types)
            {
                foreach (var interfaceType in type.GetInterfaces())
                {
                    if (interfaceType.IsGenericType &&
                        IsHandlerInterface(interfaceType.GetGenericTypeDefinition()))
                    {
                        var registration = (interfaceType, type);
                        if (seenRegistrations.Add(registration))
                        {
                            registrations.Add(registration);
                        }
                    }
                }
            }
        }

        foreach (var (service, implementation) in registrations)
        {
            services.AddTransient(service, implementation);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHandlerInterface(Type type)
    {
        return type == typeof(IRequestHandler<,>) ||
               type == typeof(IRequestHandler<>) ||
               type == typeof(INotificationHandler<>);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TryAddSingleton<TService, TImplementation>(this IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        var serviceType = typeof(TService);

        if (!services.Any(descriptor => descriptor.ServiceType == serviceType))
        {
            services.AddSingleton<TService, TImplementation>();
        }
    }
}
