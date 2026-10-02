using Mediator.Core.Wrappers;
using Microsoft.Extensions.Logging;

namespace Mediator.Core;

/// <summary>
/// Handles CreateStream operations for streaming requests with optional pipeline behaviors.
/// Responsible for dispatching streaming requests to their handlers.
/// </summary>
internal sealed class StreamRequestDispatcher : IStreamRequestDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StreamRequestDispatcher> _logger;
    private readonly bool _isDebugEnabled;

    public StreamRequestDispatcher(IServiceProvider serviceProvider, ILogger<StreamRequestDispatcher> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _isDebugEnabled = _logger.IsEnabled(LogLevel.Debug);
    }

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        if (_isDebugEnabled) _logger.LogDebug("Processing stream request {RequestType}", requestType.Name);

        try
        {
            return HandlerWrapperCache.GetStreamWrapper<TResponse>(requestType)
                .Handle(request, _serviceProvider, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream request {RequestType} failed with exception", requestType.Name);
            throw;
        }
    }
}
