using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Mediator.Tests
{
    // Test request and response types
    public class TestRequest : IRequest<string>
    {
        public string Message { get; set; } = string.Empty;
    }

    public class TestRequestWithoutResponse : IRequest
    {
        public string Message { get; set; } = string.Empty;
        public bool Handled { get; set; }
    }

    public class TestNotification : INotification
    {
        public string Message { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // Test handlers - using instance-based counting instead of static
    public class TestNotificationHandler : INotificationHandler<TestNotification>
    {
        private readonly ITestNotificationTracker _tracker;

        public TestNotificationHandler(ITestNotificationTracker tracker)
        {
            _tracker = tracker;
        }

        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            _tracker.IncrementHandleCount("TestNotificationHandler");
            return Task.CompletedTask;
        }
    }

    public class AnotherTestNotificationHandler : INotificationHandler<TestNotification>
    {
        private readonly ITestNotificationTracker _tracker;

        public AnotherTestNotificationHandler(ITestNotificationTracker tracker)
        {
            _tracker = tracker;
        }

        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            _tracker.IncrementHandleCount("AnotherTestNotificationHandler");
            return Task.CompletedTask;
        }
    }

    public class SlowTestNotificationHandler : INotificationHandler<TestNotification>
    {
        private readonly ITestNotificationTracker _tracker;

        public SlowTestNotificationHandler(ITestNotificationTracker tracker)
        {
            _tracker = tracker;
        }

        public async Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            _tracker.IncrementHandleCount("SlowTestNotificationHandler");
        }
    }

    public class FailingTestNotificationHandler : INotificationHandler<TestNotification>
    {
        private readonly ITestNotificationTracker _tracker;

        public FailingTestNotificationHandler(ITestNotificationTracker tracker)
        {
            _tracker = tracker;
        }

        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            _tracker.IncrementHandleCount("FailingTestNotificationHandler");
            throw new InvalidOperationException("Handler intentionally failed");
        }
    }

    // Interface and implementation for tracking test notifications
    public interface ITestNotificationTracker
    {
        void IncrementHandleCount(string handlerName);
        int GetHandleCount(string handlerName);
        void Reset();
    }

    public class TestNotificationTracker : ITestNotificationTracker
    {
        private readonly ConcurrentDictionary<string, int> _handleCounts = new();

        public void IncrementHandleCount(string handlerName)
        {
            _handleCounts.AddOrUpdate(handlerName, 1, (key, current) => current + 1);
        }

        public int GetHandleCount(string handlerName)
        {
            return _handleCounts.GetValueOrDefault(handlerName, 0);
        }

        public void Reset()
        {
            _handleCounts.Clear();
        }
    }

    // Test request handlers
    public class TestRequestHandler : IRequestHandler<TestRequest, string>
    {
        public Task<string> Handle(TestRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult($"Handled: {request.Message}");
        }
    }

    public class TestRequestWithoutResponseHandler : IRequestHandler<TestRequestWithoutResponse>
    {
        public Task Handle(TestRequestWithoutResponse request, CancellationToken cancellationToken)
        {
            request.Handled = true;
            return Task.CompletedTask;
        }
    }

    // Test pipeline behavior - using instance-based counting
    public class TestPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly ITestPipelineTracker _tracker;

        public TestPipelineBehavior(ITestPipelineTracker tracker)
        {
            _tracker = tracker;
        }

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            _tracker.IncrementExecuteCount();
            return await next();
        }
    }

    public interface ITestPipelineTracker
    {
        void IncrementExecuteCount();
        int GetExecuteCount();
        void Reset();
    }

    public class TestPipelineTracker : ITestPipelineTracker
    {
        private int _executeCount;

        public void IncrementExecuteCount()
        {
            Interlocked.Increment(ref _executeCount);
        }

        public int GetExecuteCount()
        {
            return _executeCount;
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _executeCount, 0);
        }
    }

    // Helper to create isolated test service provider
    public static class TestServiceProviderFactory
    {
        public static (ServiceProvider ServiceProvider, ITestNotificationTracker NotificationTracker, ITestPipelineTracker PipelineTracker) 
            CreateServiceProvider(Action<MediatorOptions>? configureOptions = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

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
            return (serviceProvider, notificationTracker, pipelineTracker);
        }
    }

    // Additional test types for edge cases
    public class ComplexNotification : INotification
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public Dictionary<string, object> Properties { get; set; } = new();
        public List<string> Tags { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public TestNotificationPriority Priority { get; set; } = TestNotificationPriority.Normal;
    }

    public enum TestNotificationPriority
    {
        Low,
        Normal,
        High,
        Critical
    }

    public class ComplexNotificationHandler : INotificationHandler<ComplexNotification>
    {
        private readonly ITestNotificationTracker _tracker;

        public ComplexNotificationHandler(ITestNotificationTracker tracker)
        {
            _tracker = tracker;
        }

        public Task Handle(ComplexNotification notification, CancellationToken cancellationToken)
        {
            _tracker.IncrementHandleCount("ComplexNotificationHandler");
            return Task.CompletedTask;
        }
    }

    // Test request with complex response
    public class ComplexTestRequest : IRequest<ComplexTestResponse>
    {
        public string Query { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    public class ComplexTestResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    }

    public class ComplexTestRequestHandler : IRequestHandler<ComplexTestRequest, ComplexTestResponse>
    {
        public Task<ComplexTestResponse> Handle(ComplexTestRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ComplexTestResponse
            {
                Success = true,
                Message = $"Processed query: {request.Query}",
                Data = request.Parameters
            });
        }
    }
}