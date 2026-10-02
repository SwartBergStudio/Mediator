using Mediator.Core.Wrappers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// Singleton that holds the dispatch logic, loggers and options shared by every DI scope.
/// The scoped dispatchers only pass in their scope's <see cref="IServiceProvider"/>, so resolving the mediator in a new
/// scope (once per web request) allocates almost nothing.
/// </summary>
internal sealed class DispatchRuntime
{
    private readonly ILogger<RequestDispatcher> _requestLogger;
    private readonly ILogger<CommandDispatcher> _commandLogger;
    private readonly ILogger<StreamRequestDispatcher> _streamLogger;
    private readonly bool _continueOnCapturedContext;

    public DispatchRuntime(
        ILogger<RequestDispatcher> requestLogger,
        ILogger<CommandDispatcher> commandLogger,
        ILogger<StreamRequestDispatcher> streamLogger,
        IOptions<MediatorOptions> options)
    {
        _requestLogger = requestLogger;
        _commandLogger = commandLogger;
        _streamLogger = streamLogger;
        _continueOnCapturedContext = !options.Value.UseConfigureAwaitGlobally;
    }

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var debug = _requestLogger.IsEnabled(LogLevel.Debug);
        if (debug) _requestLogger.LogDebug("Processing request {RequestType}", requestType.Name);

        try
        {
            var task = HandlerWrapperCache.GetRequestWrapper<TResponse>(requestType)
                .Handle(request, serviceProvider, cancellationToken);

            var result = task.IsCompletedSuccessfully
                ? task.GetAwaiter().GetResult()
                : await task.ConfigureAwait(_continueOnCapturedContext);

            if (debug) _requestLogger.LogDebug("Request {RequestType} completed successfully", requestType.Name);
            return result;
        }
        catch (Exception ex)
        {
            _requestLogger.LogError(ex, "Request {RequestType} failed with exception", requestType.Name);
            throw;
        }
    }

    public async Task SendCommand<TRequest>(TRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        // The command type is known statically, so the handler is resolved without the wrapper cache.
        var requestType = typeof(TRequest);
        var debug = _commandLogger.IsEnabled(LogLevel.Debug);
        if (debug) _commandLogger.LogDebug("Processing command {CommandType}", requestType.Name);

        try
        {
            var task = CommandHandlerWrapper<TRequest>.Handle(request, serviceProvider, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                await task.ConfigureAwait(_continueOnCapturedContext);

            if (debug) _commandLogger.LogDebug("Command {CommandType} completed successfully", requestType.Name);
        }
        catch (Exception ex)
        {
            _commandLogger.LogError(ex, "Command {CommandType} failed with exception", requestType.Name);
            throw;
        }
    }

    public async Task SendCommand(IRequest request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var debug = _commandLogger.IsEnabled(LogLevel.Debug);
        if (debug) _commandLogger.LogDebug("Processing command {CommandType}", requestType.Name);

        try
        {
            var task = HandlerWrapperCache.GetCommandWrapper(requestType).Handle(request, serviceProvider, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                await task.ConfigureAwait(_continueOnCapturedContext);

            if (debug) _commandLogger.LogDebug("Command {CommandType} completed successfully", requestType.Name);
        }
        catch (Exception ex)
        {
            _commandLogger.LogError(ex, "Command {CommandType} failed with exception", requestType.Name);
            throw;
        }
    }

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        if (_streamLogger.IsEnabled(LogLevel.Debug)) _streamLogger.LogDebug("Processing stream request {RequestType}", requestType.Name);

        try
        {
            return HandlerWrapperCache.GetStreamWrapper<TResponse>(requestType)
                .Handle(request, serviceProvider, cancellationToken);
        }
        catch (Exception ex)
        {
            _streamLogger.LogError(ex, "Stream request {RequestType} failed with exception", requestType.Name);
            throw;
        }
    }
}
