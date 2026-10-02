namespace Mediator.Core.Wrappers;

internal static class HandlerResolution
{
    /// <summary>
    /// Resolves all registrations of <typeparamref name="T"/> as an array, skipping nulls.
    /// Microsoft.Extensions.DependencyInjection already returns an array, so the common case does not copy.
    /// </summary>
    public static T[] ResolveAll<T>(IServiceProvider serviceProvider) where T : class
    {
        var services = serviceProvider.GetServices<T>();
        if (services is T[] array && Array.IndexOf(array, null) < 0)
            return array;

        var list = new List<T>();
        foreach (var service in services)
        {
            if (service != null) list.Add(service);
        }
        return list.Count == 0 ? Array.Empty<T>() : list.ToArray();
    }

    public static InvalidOperationException NotFound(Type handlerType)
        => new($"Handler not found: {handlerType.Name}");
}
