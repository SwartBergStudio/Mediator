namespace Mediator
{
    /// <summary>
    /// Declares open generic pipeline behaviors (<c>IPipelineBehavior&lt;,&gt;</c>, <c>IPipelineBehavior&lt;&gt;</c> for requests
    /// without a response, and <c>IStreamPipelineBehavior&lt;,&gt;</c>) that wrap every matching request handled in this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For each handler in the assembly, a closed behavior (for example <c>ValidationBehavior&lt;CreateUser, Guid&gt;</c>)
    /// is registered, in the order listed here, when its generic constraints allow it. The source generator emits these
    /// registrations at compile time and <c>AddMediator(assemblies)</c> applies them when it scans the assembly.
    /// </para>
    /// <para>
    /// Prefer this over <c>services.AddTransient(typeof(IPipelineBehavior&lt;,&gt;), typeof(MyBehavior&lt;,&gt;))</c> for
    /// Native AOT: the DI container cannot close open generic services over value types (for example
    /// <c>IRequest&lt;int&gt;</c>) without dynamic code. Do not register the same behavior both ways, or it runs twice.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>[assembly: MediatorPipelineBehaviors(typeof(LoggingBehavior&lt;,&gt;), typeof(ValidationBehavior&lt;,&gt;))]</code>
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class MediatorPipelineBehaviorsAttribute : Attribute
    {
        /// <summary>
        /// Initializes the attribute with behaviors in execution order (the first listed is the outermost).
        /// </summary>
        /// <param name="behaviorTypes">Open generic behavior types, e.g. <c>typeof(ValidationBehavior&lt;,&gt;)</c>.</param>
        public MediatorPipelineBehaviorsAttribute(params Type[] behaviorTypes)
        {
            BehaviorTypes = behaviorTypes ?? Array.Empty<Type>();
        }

        /// <summary>
        /// The open generic behavior types, in execution order.
        /// </summary>
        public Type[] BehaviorTypes { get; }
    }
}
