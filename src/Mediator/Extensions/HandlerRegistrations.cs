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
    /// <remarks>
    /// Behaviors are not discovered: scanning can't know the order a pipeline needs, or that a behavior is only meant for
    /// some environments. They are registered from <see cref="MediatorPipelineBehaviorsAttribute"/>, in the listed order,
    /// or explicitly by the app.
    /// </remarks>
    private static readonly Type[] s_handlerInterfaces =
    {
        typeof(IRequestHandler<,>),
        typeof(IRequestHandler<>),
        typeof(INotificationHandler<>),
        typeof(IStreamRequestHandler<,>),
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
                (IsHandlerInterface(descriptor.ServiceType) || IsBehaviorInterface(descriptor.ServiceType)))
            {
                existing.Add((descriptor.ServiceType, descriptor.ImplementationType));
            }
            else if (descriptor.ImplementationFactory?.Target is HandlerForwarder forwarder)
            {
                // A singleton or scoped handler added by AddHandler below: its descriptor has no implementation type.
                existing.Add((descriptor.ServiceType, forwarder.ImplementationType));
            }
        }
        return existing;
    }

    /// <summary>
    /// Registers one handler unless the (service, implementation) pair is already registered. A transient handler is a
    /// plain descriptor. A singleton or scoped handler class is registered once as itself, and each of its handler
    /// interfaces resolves to that registration, so a class that handles several messages is one object, not one per
    /// interface.
    /// </summary>
    public static void AddHandler(
        IServiceCollection services,
        HashSet<(Type Service, Type Implementation)> existing,
        Type serviceType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type implementationType,
        ServiceLifetime lifetime)
    {
        if (!existing.Add((serviceType, implementationType)))
            return;

        if (lifetime == ServiceLifetime.Transient)
        {
            services.Add(new ServiceDescriptor(serviceType, implementationType, lifetime));
            return;
        }

        if (!IsRegistered(services, implementationType))
            services.Add(new ServiceDescriptor(implementationType, implementationType, lifetime));

        services.Add(new ServiceDescriptor(serviceType, new HandlerForwarder(implementationType).Resolve, lifetime));
    }

    private static bool IsRegistered(IServiceCollection services, Type serviceType)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == serviceType && !descriptor.IsKeyedService)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves a handler interface to the registration of its implementation class. A named class rather than a lambda
    /// so that <see cref="CollectExisting"/> can read the implementation type back from the descriptor's factory.
    /// </summary>
    private sealed class HandlerForwarder(Type implementationType)
    {
        public Type ImplementationType { get; } = implementationType;

        public object Resolve(IServiceProvider serviceProvider) => serviceProvider.GetRequiredService(ImplementationType);
    }

    /// <summary>
    /// Scans the assemblies for closed handler implementations and registers them (transient unless
    /// <see cref="MediatorHandlerLifetimeAttribute"/> on the class or its assembly says otherwise), then applies
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
            var declaredBehaviors = assembly.GetCustomAttribute<MediatorPipelineBehaviorsAttribute>()?.BehaviorTypes ?? Array.Empty<Type>();
            var assemblyLifetime = assembly.GetCustomAttribute<MediatorHandlerLifetimeAttribute>()?.Lifetime ?? ServiceLifetime.Transient;

            foreach (var type in GetLoadableTypes(assembly))
            {
                // Open generic implementations cannot be registered against closed interfaces; they must be
                // registered explicitly or declared with [assembly: MediatorPipelineBehaviors(...)].
                if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
                    continue;

                ServiceLifetime? lifetime = null;

                foreach (var interfaceType in type.GetInterfaces())
                {
                    if (!IsHandlerInterface(interfaceType))
                        continue;

                    // Read once per handler class, and only for classes that are handlers.
                    lifetime ??= type.GetCustomAttribute<MediatorHandlerLifetimeAttribute>(inherit: false)?.Lifetime ?? assemblyLifetime;

                    if (IsClosed(interfaceType, typeof(IRequestHandler<,>)) ||
                        IsClosed(interfaceType, typeof(IRequestHandler<>)) ||
                        IsClosed(interfaceType, typeof(IStreamRequestHandler<,>)))
                        requestHandlers.Add(interfaceType);

                    AddHandler(services, existing, interfaceType, type, lifetime.Value);
                }
            }

            RegisterDeclaredBehaviors(services, declaredBehaviors, requestHandlers, existing);
        }
    }

    /// <summary>
    /// Registers the behaviors declared with <see cref="MediatorPipelineBehaviorsAttribute"/>, in declaration order: a closed
    /// behavior for the request(s) it names, and an open generic one closed over every request handled in the assembly
    /// (skipping combinations its generic constraints reject). Mirrors what the source generator emits.
    /// </summary>
    [RequiresUnreferencedCode("Closes generic behavior types via reflection.")]
    [RequiresDynamicCode("Closes generic behavior types via MakeGenericType.")]
    private static void RegisterDeclaredBehaviors(IServiceCollection services, Type[] behaviorTypes, List<Type> requestHandlers, HashSet<(Type, Type)> existing)
    {
        foreach (var behaviorType in behaviorTypes)
        {
            if (behaviorType is null || behaviorType.IsAbstract)
                continue;

            if (!behaviorType.IsGenericTypeDefinition)
            {
                foreach (var behaviorInterface in behaviorType.GetInterfaces())
                {
                    if (IsBehaviorInterface(behaviorInterface) && existing.Add((behaviorInterface, behaviorType)))
                        services.AddTransient(behaviorInterface, behaviorType);
                }
                continue;
            }

            foreach (var handlerInterface in requestHandlers)
            {
                var handlerDefinition = handlerInterface.GetGenericTypeDefinition();
                var behaviorInterface = handlerDefinition == typeof(IRequestHandler<,>) ? typeof(IPipelineBehavior<,>)
                    : handlerDefinition == typeof(IRequestHandler<>) ? typeof(IPipelineBehavior<>)
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
    /// (or <c>MyBehavior&lt;TRequest&gt; : IPipelineBehavior&lt;TRequest&gt;</c>) for the given message type(s). Returns false if the type does not implement the behavior interface with
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

    private static bool IsBehaviorInterface(Type type)
        => IsClosed(type, typeof(IPipelineBehavior<,>)) || IsClosed(type, typeof(IPipelineBehavior<>)) || IsClosed(type, typeof(IStreamPipelineBehavior<,>));

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
