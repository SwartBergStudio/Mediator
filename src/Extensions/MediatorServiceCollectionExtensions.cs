using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

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

        services.AddScoped<IMediator, Core.Mediator>();

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
                        registrations.Add((interfaceType, type));
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
