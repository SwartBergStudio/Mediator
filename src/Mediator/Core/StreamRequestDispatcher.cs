namespace Mediator.Core;

/// <summary>
/// Handles CreateStream operations for streaming requests with optional pipeline behaviors for the current DI scope.
/// </summary>
internal sealed class StreamRequestDispatcher(IServiceProvider serviceProvider) : IStreamRequestDispatcher
{
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
        => Dispatch.CreateStream(request, serviceProvider, cancellationToken);
}
