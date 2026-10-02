using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace Mediator;

/// <summary>
/// Shared handler-registration logic used by both reflection scanning and source-generated registration.
/// </summary>
internal static class HandlerRegistrations
{
    /// <summary>Interfaces whose closed implementations are discovered by assembly scanning.</summary>
    private static readonly Type[] s_handlerInterfaces =
    {
        typeof(IRequestHandler<,>),
        typeof(IRequestHandler<>),
        typeof(INotificationHandler<>),
        typeof(IStreamRequestHandler<,>),
        typeof(IStreamPipelineBehavior<,>),
    };

    /// <summary>
    /// Returns the (service, implementation) pairs already registered for mediator handlers and pipeline behaviors so
    /// that repeated registration (manual + scan, generated + scan, or AddMediator called twice) adds no duplicates.
    /// </summary>
    public static HashSet<(Type Service, Type Implementation)> CollectExisting(IServiceCollection services)
    {
        var existing = new HashSet<(Type, Type)>();
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationType != null &&
                (IsHandlerInterface(descriptor.ServiceType) || IsClosed(descriptor.ServiceType, typeof(IPipelineBehavior<,>))))
            {
                existing.Add((descriptor.ServiceType, descriptor.ImplementationType));
            }
        }
        return existing;
    }

    /// <summary>
    /// Scans the assemblies for closed handler implementations and registers them as transient services, then applies
    /// the assembly's <see cref="MediatorPipelineBehaviorsAttribute"/> to the requests handled in it.
    /// </summary>
    [RequiresUnreferencedCode("Assembly scanning discovers handler types via reflection; handlers may be trimmed. Use the SwartBerg.Mediator.SourceGenerator package for trimmed or Native AOT apps.")]
    [RequiresDynamicCode("Closing open generic pipeline behaviors requires dynamic code. Use the SwartBerg.Mediator.SourceGenerator package for Native AOT apps.")]
    public static void RegisterFromAssemblies(IServiceCollection services, Assembly[] assemblies)
    {
        if (assemblies.Length == 0) return;

        var existing = CollectExisting(services);

        foreach (var assembly in assemblies)
        {
            var requestHandlers = new List<Type>();

            foreach (var type in GetLoadableTypes(assembly))
            {
                // Open generic implementations cannot be registered against closed interfaces; they must be
                // registered explicitly or declared with [assembly: MediatorPipelineBehaviors(...)].
                if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
                    continue;

                foreach (var interfaceType in type.GetInterfaces())
                {
                    if (!IsHandlerInterface(interfaceType))
                        continue;

                    if (IsClosed(interfaceType, typeof(IRequestHandler<,>)) || IsClosed(interfaceType, typeof(IStreamRequestHandler<,>)))
                        requestHandlers.Add(interfaceType);

                    if (existing.Add((interfaceType, type)))
                        services.AddTransient(interfaceType, type);
                }
            }

            RegisterDeclaredBehaviors(services, assembly, requestHandlers, existing);
        }
    }

    /// <summary>
    /// Closes each behavior declared with <see cref="MediatorPipelineBehaviorsAttribute"/> over every request handled in
    /// the assembly (skipping combinations its generic constraints reject) and registers it, in declaration order.
    /// Mirrors what the source generator emits.
    /// </summary>
    [RequiresUnreferencedCode("Closes generic behavior types via reflection.")]
    [RequiresDynamicCode("Closes generic behavior types via MakeGenericType.")]
    private static void RegisterDeclaredBehaviors(IServiceCollection services, Assembly assembly, List<Type> requestHandlers, HashSet<(Type, Type)> existing)
    {
        var behaviorTypes = assembly.GetCustomAttribute<MediatorPipelineBehaviorsAttribute>()?.BehaviorTypes;
        if (behaviorTypes is null || behaviorTypes.Length == 0 || requestHandlers.Count == 0)
            return;

        foreach (var behaviorType in behaviorTypes)
        {
            foreach (var handlerInterface in requestHandlers)
            {
                var behaviorInterface = handlerInterface.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                    ? typeof(IPipelineBehavior<,>)
                    : typeof(IStreamPipelineBehavior<,>);

                var messageTypes = handlerInterface.GetGenericArguments();
                if (TryCloseBehavior(behaviorType, behaviorInterface, messageTypes, out var closedBehavior))
                {
                    var serviceType = behaviorInterface.MakeGenericType(messageTypes);
                    if (existing.Add((serviceType, closedBehavior)))
                        services.AddTransient(serviceType, closedBehavior);
                }
            }
        }
    }

    /// <summary>
    /// Closes an open behavior such as <c>MyBehavior&lt;TRequest, TResponse&gt; : IPipelineBehavior&lt;TRequest, TResponse&gt;</c>
    /// for the given (request, response) pair. Returns false if the type does not implement the behavior interface with
    /// its own type parameters, or if its constraints reject the pair.
    /// </summary>
    [RequiresUnreferencedCode("Closes generic behavior types via reflection.")]
    [RequiresDynamicCode("Closes generic behavior types via MakeGenericType.")]
    private static bool TryCloseBehavior(Type behaviorType, Type behaviorInterface, Type[] messageTypes, [NotNullWhen(true)] out Type? closedBehavior)
    {
        closedBehavior = null;
        if (!behaviorType.IsGenericTypeDefinition || behaviorType.IsAbstract)
            return false;

        var typeParameters = behaviorType.GetGenericArguments();
        var implemented = behaviorType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == behaviorInterface);
        if (implemented is null || typeParameters.Length != messageTypes.Length)
            return false;

        // Map each of the behavior's type parameters to the request/response type in the matching interface position.
        var interfaceArguments = implemented.GetGenericArguments();
        var arguments = new Type[typeParameters.Length];
        for (var i = 0; i < typeParameters.Length; i++)
        {
            var position = Array.IndexOf(interfaceArguments, typeParameters[i]);
            if (position < 0) return false;
            arguments[i] = messageTypes[position];
        }

        try
        {
            closedBehavior = behaviorType.MakeGenericType(arguments);
            return true;
        }
        catch (ArgumentException)
        {
            // Generic constraints are not satisfied for this request; the behavior does not apply to it.
            return false;
        }
    }

    private static bool IsHandlerInterface(Type type)
    {
        if (!type.IsGenericType || type.IsGenericTypeDefinition)
            return false;

        return Array.IndexOf(s_handlerInterfaces, type.GetGenericTypeDefinition()) >= 0;
    }

    private static bool IsClosed(Type type, Type genericDefinition)
        => type.IsGenericType && !type.IsGenericTypeDefinition && type.GetGenericTypeDefinition() == genericDefinition;

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
