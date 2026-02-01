namespace Mediator.Core;

/// <summary>
/// Default implementation of IScopeProvider using IServiceProvider.CreateScope().
/// </summary>
public sealed class DefaultScopeProvider : IScopeProvider
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the DefaultScopeProvider.
    /// </summary>
    /// <param name="serviceProvider">The root service provider.</param>
    public DefaultScopeProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Creates a new service scope using the underlying service provider.
    /// </summary>
    public IServiceScope CreateScope() => _serviceProvider.CreateScope();
}
