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
    public static Task Handle(TRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handler = serviceProvider.GetService<IRequestHandler<TRequest>>()
            ?? throw HandlerResolution.NotFound(typeof(IRequestHandler<TRequest>));

        var behaviors = HandlerResolution.ResolveAll<IPipelineBehavior<TRequest>>(serviceProvider);
        if (behaviors.Length == 0)
            return handler.Handle(request, cancellationToken);

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
