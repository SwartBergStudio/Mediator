using Microsoft.Extensions.DependencyInjection;

namespace Mediator.Tests
{
    public abstract class MediatorTestBase : IDisposable
    {
        private ServiceProvider? _serviceProvider;

        protected ServiceProvider TrackServiceProvider(ServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            return serviceProvider;
        }

        public virtual void Dispose()
        {
            _serviceProvider?.Dispose();
        }
    }
}
