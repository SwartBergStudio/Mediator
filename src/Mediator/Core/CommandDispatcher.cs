namespace Mediator.Core;

/// <summary>
/// Handles Send (command) operations without responses for the current DI scope.
/// </summary>
internal sealed class CommandDispatcher(IServiceProvider serviceProvider, DispatchRuntime runtime) : ICommandDispatcher
{
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
        => runtime.SendCommand(request, serviceProvider, cancellationToken);

    public Task Send(IRequest request, CancellationToken cancellationToken = default)
        => runtime.SendCommand(request, serviceProvider, cancellationToken);
}
