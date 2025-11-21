using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Mediator.Tests
{
    public static class TestFactory
    {
        public static (IMediator mediator, ServiceProvider serviceProvider) CreateSimpleMediator(Action<MediatorOptions>? configure = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            
            if (configure != null)
            {
                services.Configure(configure);
            }
            else
            {
                services.Configure<MediatorOptions>(options =>
                {
                    options.EnablePersistence = false;
                    options.NotificationWorkerCount = 1;
                });
            }
            
            services.AddMediator();
            
            var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();
            
            return (mediator, serviceProvider);
        }

        public static (IMediator mediator, ServiceProvider serviceProvider, TestTracker tracker) CreateMediatorWithTracking(Action<MediatorOptions>? configure = null)
        {
            var services = new ServiceCollection();
            var tracker = new TestTracker();
            
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
            services.AddSingleton(tracker);
            
            services.AddTransient<IRequestHandler<TestCommand>, TestCommandHandler>();
            services.AddTransient<IRequestHandler<TestQuery, string>, TestQueryHandler>();
            services.AddTransient<INotificationHandler<TestEvent>, TestEventHandler1>();
            services.AddTransient<INotificationHandler<TestEvent>, TestEventHandler2>();
            services.AddTransient<INotificationHandler<TestEvent>, TestEventHandlerThatFails>();
            
            services.AddTransient<IPipelineBehavior<TestQuery, string>, TestLoggingPipelineBehavior>();
            services.AddTransient<IPipelineBehavior<TestQuery, string>, TestValidationPipelineBehavior>();
            
            if (configure != null)
            {
                services.Configure(configure);
            }
            else
            {
                services.Configure<MediatorOptions>(options =>
                {
                    options.EnablePersistence = false;
                    options.NotificationWorkerCount = 1;
                });
            }
            
            services.AddMediator();
            
            var serviceProvider = services.BuildServiceProvider();
            var mediator = serviceProvider.GetRequiredService<IMediator>();
            
            return (mediator, serviceProvider, tracker);
        }
    }

    public class TestTracker
    {
        private readonly ConcurrentDictionary<string, int> _counters = new();
        private readonly ConcurrentBag<string> _events = new();
        private readonly object _lock = new();

        public void IncrementCounter(string name) => _counters.AddOrUpdate(name, 1, (k, v) => v + 1);
        public int GetCounter(string name) => _counters.TryGetValue(name, out var count) ? count : 0;
        public void AddEvent(string eventName) => _events.Add(eventName);
        public List<string> GetEvents() { lock (_lock) { return new List<string>(_events); } }
        public void Reset() 
        { 
            _counters.Clear(); 
            lock (_lock) { _events.Clear(); }
        }
    }

    public class TestCommand : IRequest
    {
        public string Data { get; set; } = string.Empty;
        public bool WasHandled { get; set; }
    }

    public class TestQuery : IRequest<string>
    {
        public string Query { get; set; } = string.Empty;
        public bool ShouldFail { get; set; }
    }

    public class TestEvent : INotification
    {
        public string Message { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class TestCommandHandler : IRequestHandler<TestCommand>
    {
        private readonly TestTracker _tracker;

        public TestCommandHandler(TestTracker tracker) => _tracker = tracker;

        public Task Handle(TestCommand request, CancellationToken cancellationToken)
        {
            _tracker.IncrementCounter("CommandHandled");
            _tracker.AddEvent($"Command processed: {request.Data}");
            request.WasHandled = true;
            return Task.CompletedTask;
        }
    }

    public class TestQueryHandler : IRequestHandler<TestQuery, string>
    {
        private readonly TestTracker _tracker;

        public TestQueryHandler(TestTracker tracker) => _tracker = tracker;

        public Task<string> Handle(TestQuery request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            _tracker.IncrementCounter("QueryHandled");
            _tracker.AddEvent($"Query processed: {request.Query}");
            
            return Task.FromResult($"Result for: {request.Query}");
        }
    }

    public class TestEventHandler1 : INotificationHandler<TestEvent>
    {
        private readonly TestTracker _tracker;

        public TestEventHandler1(TestTracker tracker) => _tracker = tracker;

        public Task Handle(TestEvent notification, CancellationToken cancellationToken)
        {
            _tracker.IncrementCounter("EventHandler1");
            _tracker.AddEvent($"Handler1 processed: {notification.Message}");
            return Task.CompletedTask;
        }
    }

    public class TestEventHandler2 : INotificationHandler<TestEvent>
    {
        private readonly TestTracker _tracker;

        public TestEventHandler2(TestTracker tracker) => _tracker = tracker;

        public Task Handle(TestEvent notification, CancellationToken cancellationToken)
        {
            _tracker.IncrementCounter("EventHandler2");
            _tracker.AddEvent($"Handler2 processed: {notification.Message}");
            return Task.CompletedTask;
        }
    }

    public class TestEventHandlerThatFails : INotificationHandler<TestEvent>
    {
        private readonly TestTracker _tracker;

        public TestEventHandlerThatFails(TestTracker tracker) => _tracker = tracker;

        public Task Handle(TestEvent notification, CancellationToken cancellationToken)
        {
            _tracker.IncrementCounter("FailingEventHandler");
            _tracker.AddEvent($"Failing handler called: {notification.Message}");
            throw new InvalidOperationException("Handler intentionally failed");
        }
    }

    public class TestLoggingPipelineBehavior : IPipelineBehavior<TestQuery, string>
    {
        private readonly TestTracker _tracker;

        public TestLoggingPipelineBehavior(TestTracker tracker) => _tracker = tracker;

        public async Task<string> Handle(TestQuery request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            _tracker.AddEvent("Logging: Before handler");
            _tracker.IncrementCounter("LoggingBehavior");
            
            var response = await next();
            
            _tracker.AddEvent("Logging: After handler");
            return response;
        }
    }

    public class TestValidationPipelineBehavior : IPipelineBehavior<TestQuery, string>
    {
        private readonly TestTracker _tracker;

        public TestValidationPipelineBehavior(TestTracker tracker) => _tracker = tracker;

        public async Task<string> Handle(TestQuery request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            _tracker.AddEvent("Validation: Validating request");
            _tracker.IncrementCounter("ValidationBehavior");

            if (request.ShouldFail)
            {
                _tracker.AddEvent("Validation: Request failed validation");
                throw new ArgumentException("Request failed validation");
            }

            _tracker.AddEvent("Validation: Request passed validation");
            return await next();
        }
    }
}