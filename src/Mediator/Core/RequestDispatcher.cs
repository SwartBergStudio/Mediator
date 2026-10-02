using Mediator.Core.Wrappers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// Handles Send&lt;TResponse&gt; operations with optional pipeline behaviors.
/// Responsible for request dispatching and response handling.
/// </summary>
internal sealed class RequestDispatcher : IRequestDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RequestDispatcher> _logger;
    private readonly bool _continueOnCapturedContext;
    private readonly bool _isDebugEnabled;

    public RequestDispatcher(IServiceProvider serviceProvider, ILogger<RequestDispatcher> logger, IOptions<MediatorOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _continueOnCapturedContext = !options.Value.UseConfigureAwaitGlobally;
        _isDebugEnabled = _logger.IsEnabled(LogLevel.Debug);
    }

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        if (_isDebugEnabled) _logger.LogDebug("Processing request {RequestType}", requestType.Name);

        try
        {
            var task = HandlerWrapperCache.GetRequestWrapper<TResponse>(requestType)
                .Handle(request, _serviceProvider, cancellationToken);

            var result = task.IsCompletedSuccessfully
                ? task.GetAwaiter().GetResult()
                : await task.ConfigureAwait(_continueOnCapturedContext);

            if (_isDebugEnabled) _logger.LogDebug("Request {RequestType} completed successfully", requestType.Name);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request {RequestType} failed with exception", requestType.Name);
            throw;
        }
    }
}
