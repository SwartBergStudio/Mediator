namespace Mediator
{
    /// <summary>
    /// Sets the DI lifetime that <c>AddMediator(assemblies)</c> and the source-generated <c>AddMediatorHandlers()</c> give
    /// to handlers. Without it handlers are transient, as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a handler class it applies to that class. On an assembly it applies to every handler in the assembly that has
    /// no attribute of its own. It covers request, command, stream and notification handlers; pipeline behaviors stay
    /// transient.
    /// </para>
    /// <para>
    /// A singleton or scoped handler class is created once (per container, or per scope) however many handler
    /// interfaces it implements, so one object can keep state across the messages it handles.
    /// </para>
    /// <para>
    /// Two cautions for <see cref="ServiceLifetime.Singleton"/>. The handler must be thread-safe if its notifications
    /// are sent with <c>Publish</c>: background workers run handlers at the same time. And it must not depend on scoped
    /// services (for example a <c>DbContext</c>): it would keep the first one it was given for the life of the app.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [assembly: MediatorHandlerLifetime(ServiceLifetime.Singleton)]   // every handler in this assembly
    ///
    /// [MediatorHandlerLifetime(ServiceLifetime.Singleton)]             // or one handler
    /// public sealed class ScoreBoard : INotificationHandler&lt;UnitDied&gt;, INotificationHandler&lt;BuildingCompleted&gt; { }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class MediatorHandlerLifetimeAttribute : Attribute
    {
        /// <summary>
        /// Initializes the attribute with the lifetime to register handlers with.
        /// </summary>
        /// <param name="lifetime">The DI lifetime of the handler(s).</param>
        public MediatorHandlerLifetimeAttribute(ServiceLifetime lifetime)
        {
            Lifetime = lifetime;
        }

        /// <summary>
        /// The DI lifetime of the handler(s).
        /// </summary>
        public ServiceLifetime Lifetime { get; }
    }
}
