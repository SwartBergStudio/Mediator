namespace Mediator.Core.Wrappers;

/// <summary>
/// Strongly-typed entry point for a request/response pair. One instance exists per closed request type
/// and is cached for the lifetime of the process, so no reflection is needed on the hot path.
/// </summary>
internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>()
            ?? throw HandlerResolution.NotFound(typeof(IRequestHandler<TRequest, TResponse>));

        var behaviors = HandlerResolution.ResolveAll<IPipelineBehavior<TRequest, TResponse>>(serviceProvider);
        if (behaviors.Length == 0)
            return handler.Handle(typedRequest, cancellationToken);

        RequestHandlerDelegate<TResponse> next = () => handler.Handle(typedRequest, cancellationToken);
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var inner = next;
            next = () => behavior.Handle(typedRequest, inner, cancellationToken);
        }

        return next();
    }
}
