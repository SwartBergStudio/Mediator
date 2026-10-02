namespace Mediator.Core;

/// <summary>
/// Handles Send&lt;TResponse&gt; operations with optional pipeline behaviors for the current DI scope.
/// </summary>
internal sealed class RequestDispatcher(IServiceProvider serviceProvider, DispatchRuntime runtime) : IRequestDispatcher
{
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => runtime.Send(request, serviceProvider, cancellationToken);
}
