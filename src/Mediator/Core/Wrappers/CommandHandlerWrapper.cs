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
    /// Invokes the handler for a statically known command type without going through the wrapper cache.
    /// </summary>
    public static Task Handle(TRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handler = serviceProvider.GetService<IRequestHandler<TRequest>>()
            ?? throw HandlerResolution.NotFound(typeof(IRequestHandler<TRequest>));

        return handler.Handle(request, cancellationToken);
    }
}
