namespace Mediator.Core.Wrappers;

/// <summary>
/// Strongly-typed entry point for a streaming request.
/// </summary>
internal abstract class StreamRequestHandlerWrapper<TResponse>
{
    public abstract IAsyncEnumerable<TResponse> Handle(IStreamRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class StreamRequestHandlerWrapper<TRequest, TResponse> : StreamRequestHandlerWrapper<TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    public override IAsyncEnumerable<TResponse> Handle(IStreamRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = serviceProvider.GetService<IStreamRequestHandler<TRequest, TResponse>>()
            ?? throw HandlerResolution.NotFound(typeof(IStreamRequestHandler<TRequest, TResponse>));

        var behaviors = HandlerResolution.ResolveAll<IStreamPipelineBehavior<TRequest, TResponse>>(serviceProvider);
        return behaviors.Length == 0
            ? handler.Handle(typedRequest, cancellationToken)
            : InvokePipeline(handler, behaviors, typedRequest, cancellationToken);
    }

    // Separate method on purpose: the lambdas capture the parameters, and C# allocates that closure on entry to the
    // method declaring them, so in Handle it would cost an allocation on every call, even without behaviors.
    private static IAsyncEnumerable<TResponse> InvokePipeline(IStreamRequestHandler<TRequest, TResponse> handler, IStreamPipelineBehavior<TRequest, TResponse>[] behaviors, TRequest typedRequest, CancellationToken cancellationToken)
    {
        StreamHandlerDelegate<TResponse> next = () => handler.Handle(typedRequest, cancellationToken);
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var inner = next;
            next = () => behavior.Handle(typedRequest, inner, cancellationToken);
        }

        return next();
    }
}
