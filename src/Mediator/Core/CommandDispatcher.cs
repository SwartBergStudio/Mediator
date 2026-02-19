using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mediator.Core;

/// <summary>
/// Handles Send (command) operations without responses.
/// Responsible for command dispatching.
/// </summary>
internal sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly IScopeProvider _scopeProvider;
    private readonly ILogger<CommandDispatcher> _logger;
    private readonly MediatorOptions _options;
    private readonly bool _isDebugEnabled;

    private readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> _commandInvokers = new();
    private readonly ConcurrentDictionary<Type, Type> _handlerTypeCache = new();

    private static readonly MethodInfo s_invokeCommandHandlerMethod = typeof(CommandDispatcher).GetMethod(nameof(InvokeCommandHandler), BindingFlags.NonPublic | BindingFlags.Static)!;

    public CommandDispatcher(IScopeProvider scopeProvider, ILogger<CommandDispatcher> logger, IOptions<MediatorOptions> options)
    {
        _scopeProvider = scopeProvider;
        _logger = logger;
        _options = options.Value;
        _isDebugEnabled = _logger.IsEnabled(LogLevel.Debug);
    }

    public async Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        var requestType = typeof(TRequest);
        if (_isDebugEnabled) _logger.LogDebug("Processing command {CommandType}", requestType.Name);

        try
        {
            using var scope = _scopeProvider.CreateScope();
            var scopedProvider = scope.ServiceProvider;
            var invoker = GetOrCreateCommandInvoker(requestType);
            var handler = GetScopedHandler(requestType, scopedProvider);

            var task = invoker(handler, request!, cancellationToken);
            if (task.IsCompletedSuccessfully)
            {
                if (_isDebugEnabled) _logger.LogDebug("Command {CommandType} completed successfully", requestType.Name);
                return;
            }
            await InternalHelpers.AwaitConfigurable(task, _options.UseConfigureAwaitGlobally);
            if (_isDebugEnabled) _logger.LogDebug("Command {CommandType} completed successfully", requestType.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandType} failed with exception", requestType.Name);
            throw;
        }
    }

    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        var requestType = request.GetType();
        if (_isDebugEnabled) _logger.LogDebug("Processing command {CommandType}", requestType.Name);

        try
        {
            using var scope = _scopeProvider.CreateScope();
            var scopedProvider = scope.ServiceProvider;
            var invoker = GetOrCreateCommandInvoker(requestType);
            var handler = GetScopedHandler(requestType, scopedProvider);

            var task = invoker(handler, request, cancellationToken);
            if (task.IsCompletedSuccessfully)
            {
                if (_isDebugEnabled) _logger.LogDebug("Command {CommandType} completed successfully", requestType.Name);
                return;
            }
            await InternalHelpers.AwaitConfigurable(task, _options.UseConfigureAwaitGlobally);
            if (_isDebugEnabled) _logger.LogDebug("Command {CommandType} completed successfully", requestType.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandType} failed with exception", requestType.Name);
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<object, object, CancellationToken, Task> GetOrCreateCommandInvoker(Type requestType)
    {
        return _commandInvokers.GetOrAdd(requestType, _ =>
        {
            var generic = s_invokeCommandHandlerMethod.MakeGenericMethod(requestType);
            return (Func<object, object, CancellationToken, Task>)generic.CreateDelegate(typeof(Func<object, object, CancellationToken, Task>));
        });
    }

    private static Task InvokeCommandHandler<TRequest>(object handlerObj, object requestObj, CancellationToken token)
        where TRequest : IRequest
    {
        var handler = (IRequestHandler<TRequest>)handlerObj;
        return handler.Handle((TRequest)requestObj, token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object GetScopedHandler(Type requestType, IServiceProvider scopedProvider)
    {
        var handlerType = _handlerTypeCache.GetOrAdd(requestType,
            t => typeof(IRequestHandler<>).MakeGenericType(t));
        var handler = scopedProvider.GetService(handlerType);
        if (handler == null)
        {
            _logger.LogError("Handler not found for command type {CommandType}", requestType.Name);
            throw new InvalidOperationException($"Handler not found: {handlerType.Name}");
        }
        if (_isDebugEnabled) _logger.LogDebug("Resolved handler for command {CommandType}", requestType.Name);
        return handler;
    }
}
