namespace Mediator.Core.Wrappers;

/// <summary>
/// Strongly-typed entry point for a command (request without a response).
/// </summary>
internal abstract class CommandHandlerWrapper
{
    public abstract Task Handle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class CommandHandlerWrapper<TRequest> : CommandHandlerWrapper
    where TRequest : IRequest
{
    public override Task Handle(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => Handle((TRequest)request, serviceProvider, cancellationToken);

    /// <summary>
    /// Invokes the handler, wrapped in its <see cref="IPipelineBehavior{TRequest}"/>s, for a statically known command
    /// type without going through the wrapper cache.
    /// </summary>
    /// <remarks>
    /// When <typeparamref name="TRequest"/> has no handler but the command's actual type differs (a command held as an
    /// interface or base type), the actual type's handler is used, as <see cref="IMediator.Send(IRequest, CancellationToken)"/>
    /// does. Only reached when there is no handler, so it costs nothing on the normal path and changes no call that worked.
    /// </remarks>
    public static Task Handle(TRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handler = serviceProvider.GetService<IRequestHandler<TRequest>>();
        if (handler is null)
        {
            var actualType = request.GetType();
            if (actualType != typeof(TRequest) && HandlerWrapperCache.FindCommandWrapper(actualType) is { } actual)
                return actual.Handle(request, serviceProvider, cancellationToken);

            throw HandlerResolution.NotFound(typeof(IRequestHandler<TRequest>));
        }

        var behaviors = HandlerResolution.ResolveAll<IPipelineBehavior<TRequest>>(serviceProvider);
        return behaviors.Length == 0
            ? handler.Handle(request, cancellationToken)
            : InvokePipeline(handler, behaviors, request, cancellationToken);
    }

    // Separate method on purpose: the lambdas capture the parameters, and C# allocates that closure on entry to the
    // method declaring them, so in Handle it would cost an allocation on every call, even without behaviors.
    private static Task InvokePipeline(IRequestHandler<TRequest> handler, IPipelineBehavior<TRequest>[] behaviors, TRequest request, CancellationToken cancellationToken)
    {
        RequestHandlerDelegate next = () => handler.Handle(request, cancellationToken);
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var inner = next;
            next = () => behavior.Handle(request, inner, cancellationToken);
        }

        return next();
    }
}
