using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mediator.Tests
{
    /// <summary>
    /// Base class for tests that provides clean, isolated service provider creation
    /// </summary>
    public abstract class MediatorTestBase : IDisposable
    {
        private ServiceProvider? _serviceProvider;
        private ITestNotificationTracker? _notificationTracker;
        private ITestPipelineTracker? _pipelineTracker;

        protected (ServiceProvider ServiceProvider, ITestNotificationTracker NotificationTracker, ITestPipelineTracker PipelineTracker)
            CreateIsolatedServiceProvider(Action<MediatorOptions>? configureOptions = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Error)); // Reduced logging for tests

            // Register isolated trackers for this test instance
            var notificationTracker = new TestNotificationTracker();
            var pipelineTracker = new TestPipelineTracker();
            
            services.AddSingleton<ITestNotificationTracker>(notificationTracker);
            services.AddSingleton<ITestPipelineTracker>(pipelineTracker);

            if (configureOptions != null)
            {
                services.AddMediator(configureOptions, typeof(TestServiceProviderFactory).Assembly);
            }
            else
            {
                services.AddMediator(typeof(TestServiceProviderFactory).Assembly);
            }

            // Add pipeline behavior
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TestPipelineBehavior<,>));

            var serviceProvider = services.BuildServiceProvider();
            
            // Keep reference for disposal
            _serviceProvider = serviceProvider;
            _notificationTracker = notificationTracker;
            _pipelineTracker = pipelineTracker;
            
            return (serviceProvider, notificationTracker, pipelineTracker);
        }

        /// <summary>
        /// Creates a simple service provider for basic tests without complex dependencies
        /// </summary>
        protected ServiceProvider CreateSimpleServiceProvider(Action<MediatorOptions>? configureOptions = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));

            if (configureOptions != null)
            {
                services.AddMediator(configureOptions);
            }
            else
            {
                services.AddMediator();
            }

            var serviceProvider = services.BuildServiceProvider();
            _serviceProvider = serviceProvider;
            return serviceProvider;
        }

        public virtual void Dispose()
        {
            _serviceProvider?.Dispose();
        }
    }
}