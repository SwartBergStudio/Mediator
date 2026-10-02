using Mediator.Core.Wrappers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// Handles Send (command) operations without responses.
/// Responsible for command dispatching.
/// </summary>
internal sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CommandDispatcher> _logger;
    private readonly bool _continueOnCapturedContext;
    private readonly bool _isDebugEnabled;

    public CommandDispatcher(IServiceProvider serviceProvider, ILogger<CommandDispatcher> logger, IOptions<MediatorOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _continueOnCapturedContext = !options.Value.UseConfigureAwaitGlobally;
        _isDebugEnabled = _logger.IsEnabled(LogLevel.Debug);
    }

    public async Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        // The command type is known statically, so the handler is resolved without the wrapper cache.
        var requestType = typeof(TRequest);
        LogStarted(requestType);

        try
        {
            var task = CommandHandlerWrapper<TRequest>.Handle(request, _serviceProvider, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                await task.ConfigureAwait(_continueOnCapturedContext);

            LogCompleted(requestType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandType} failed with exception", requestType.Name);
            throw;
        }
    }

    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        LogStarted(requestType);

        try
        {
            var task = HandlerWrapperCache.GetCommandWrapper(requestType).Handle(request, _serviceProvider, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                await task.ConfigureAwait(_continueOnCapturedContext);

            LogCompleted(requestType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandType} failed with exception", requestType.Name);
            throw;
        }
    }

    private void LogStarted(Type requestType)
    {
        if (_isDebugEnabled) _logger.LogDebug("Processing command {CommandType}", requestType.Name);
    }

    private void LogCompleted(Type requestType)
    {
        if (_isDebugEnabled) _logger.LogDebug("Command {CommandType} completed successfully", requestType.Name);
    }
}
