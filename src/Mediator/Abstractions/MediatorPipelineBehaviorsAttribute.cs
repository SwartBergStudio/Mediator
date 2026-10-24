namespace Mediator
{
    /// <summary>
    /// Declares the pipeline behaviors of this assembly, in execution order: <c>IPipelineBehavior&lt;,&gt;</c>,
    /// <c>IPipelineBehavior&lt;&gt;</c> for requests without a response, and <c>IStreamPipelineBehavior&lt;,&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An open generic behavior (for example <c>typeof(ValidationBehavior&lt;,&gt;)</c>) is closed over each request handled in
    /// the assembly, for example <c>ValidationBehavior&lt;CreateUser, Guid&gt;</c>, when its generic constraints allow it. A
    /// closed behavior (for example <c>typeof(CachingBehavior)</c> implementing <c>IPipelineBehavior&lt;GetUser, User&gt;</c>)
    /// is registered for the request(s) it implements. Open and closed behaviors can be mixed; all are registered in the
    /// order listed here. The source generator emits these registrations at compile time and <c>AddMediator(assemblies)</c>
    /// applies them when it scans the assembly.
    /// </para>
    /// <para>
    /// Prefer this over <c>services.AddTransient(typeof(IPipelineBehavior&lt;,&gt;), typeof(MyBehavior&lt;,&gt;))</c> for
    /// Native AOT: the DI container cannot close open generic services over value types (for example
    /// <c>IRequest&lt;int&gt;</c>) without dynamic code. Do not register the same behavior both ways, or it runs twice.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>[assembly: MediatorPipelineBehaviors(typeof(LoggingBehavior&lt;,&gt;), typeof(CachingBehavior), typeof(ValidationBehavior&lt;,&gt;))]</code>
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class MediatorPipelineBehaviorsAttribute : Attribute
    {
        /// <summary>
        /// Initializes the attribute with behaviors in execution order (the first listed is the outermost).
        /// </summary>
        /// <param name="behaviorTypes">Behavior types: open generic, e.g. <c>typeof(ValidationBehavior&lt;,&gt;)</c>, or closed, e.g. <c>typeof(CachingBehavior)</c>.</param>
        public MediatorPipelineBehaviorsAttribute(params Type[] behaviorTypes)
        {
            BehaviorTypes = behaviorTypes ?? Array.Empty<Type>();
        }

        /// <summary>
        /// The behavior types, in execution order.
        /// </summary>
        public Type[] BehaviorTypes { get; }
    }
}
