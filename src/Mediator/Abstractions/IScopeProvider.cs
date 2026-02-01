namespace Mediator;

/// <summary>
/// Provides dependency injection scopes for request/command processing.
/// Abstracts scope creation to enable custom scope strategies and improve testability.
/// </summary>
public interface IScopeProvider
{
    /// <summary>
    /// Creates a new service scope for isolated request/command handling.
    /// </summary>
    /// <returns>A service scope that must be disposed after use.</returns>
    IServiceScope CreateScope();
}
