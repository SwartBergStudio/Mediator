namespace Mediator.Core;

/// <summary>
/// Handles Send&lt;TResponse&gt; operations with optional pipeline behaviors for the current DI scope.
/// </summary>
internal sealed class RequestDispatcher(IServiceProvider serviceProvider) : IRequestDispatcher
{
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => Dispatch.Send(request, serviceProvider, cancellationToken);
}
