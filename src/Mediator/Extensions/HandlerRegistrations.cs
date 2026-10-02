using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace Mediator;

/// <summary>
/// Shared handler-registration logic used by both reflection scanning and source-generated registration.
/// </summary>
internal static class HandlerRegistrations
{
    private static readonly Type[] s_handlerInterfaces =
    {
        typeof(IRequestHandler<,>),
        typeof(IRequestHandler<>),
        typeof(INotificationHandler<>),
        typeof(IStreamRequestHandler<,>),
        typeof(IStreamPipelineBehavior<,>),
    };

    /// <summary>
    /// Returns the (service, implementation) pairs already registered for mediator handler interfaces so that
    /// repeated registration (manual + scan, or AddMediator called twice) does not duplicate handlers.
    /// </summary>
    public static HashSet<(Type Service, Type Implementation)> CollectExisting(IServiceCollection services)
    {
        var existing = new HashSet<(Type, Type)>();
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationType != null && IsHandlerInterface(descriptor.ServiceType))
            {
                existing.Add((descriptor.ServiceType, descriptor.ImplementationType));
            }
        }
        return existing;
    }

    /// <summary>
    /// Scans the assemblies for closed handler implementations and registers them as transient services.
    /// </summary>
    [RequiresUnreferencedCode("Assembly scanning discovers handler types via reflection; handlers may be trimmed. Use the SwartBerg.Mediator.SourceGenerator package for trimmed or Native AOT apps.")]
    public static void RegisterFromAssemblies(IServiceCollection services, Assembly[] assemblies)
    {
        if (assemblies.Length == 0) return;

        var existing = CollectExisting(services);

        foreach (var assembly in assemblies)
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                // Open generic implementations cannot be registered against closed interfaces; they must be
                // registered explicitly (e.g. services.AddTransient(typeof(IPipelineBehavior<,>), typeof(MyBehavior<,>))).
                if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
                    continue;

                foreach (var interfaceType in type.GetInterfaces())
                {
                    if (IsHandlerInterface(interfaceType) && existing.Add((interfaceType, type)))
                    {
                        services.AddTransient(interfaceType, type);
                    }
                }
            }
        }
    }

    private static bool IsHandlerInterface(Type type)
    {
        if (!type.IsGenericType || type.IsGenericTypeDefinition)
            return false;

        return Array.IndexOf(s_handlerInterfaces, type.GetGenericTypeDefinition()) >= 0;
    }

    [RequiresUnreferencedCode("Enumerates all types of the assembly.")]
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types could not be loaded (e.g. missing optional dependencies); register the ones that could.
            return ex.Types.Where(t => t != null)!;
        }
    }
}
