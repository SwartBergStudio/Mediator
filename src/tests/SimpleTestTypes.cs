using System.Collections.Concurrent;

namespace Mediator.Tests
{
    // Simplified test handlers that don't require constructor dependencies
    public class SimpleTestNotificationHandler : INotificationHandler<TestNotification>
    {
        public static readonly ConcurrentDictionary<string, int> HandlerCounts = new();
        
        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            HandlerCounts.AddOrUpdate("SimpleTestNotificationHandler", 1, (key, current) => current + 1);
            return Task.CompletedTask;
        }
    }

    public class SimpleAnotherTestNotificationHandler : INotificationHandler<TestNotification>
    {
        public static readonly ConcurrentDictionary<string, int> HandlerCounts = new();
        
        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            HandlerCounts.AddOrUpdate("SimpleAnotherTestNotificationHandler", 1, (key, current) => current + 1);
            return Task.CompletedTask;
        }
    }

    public class SimpleFailingTestNotificationHandler : INotificationHandler<TestNotification>
    {
        public static readonly ConcurrentDictionary<string, int> HandlerCounts = new();
        
        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            HandlerCounts.AddOrUpdate("SimpleFailingTestNotificationHandler", 1, (key, current) => current + 1);
            throw new InvalidOperationException("Handler intentionally failed");
        }
    }

    // Test request and response types
    public class SimpleTestRequest : IRequest<string>
    {
        public string Message { get; set; } = string.Empty;
    }

    public class SimpleTestRequestWithoutResponse : IRequest
    {
        public string Message { get; set; } = string.Empty;
        public bool Handled { get; set; }
    }

    public class SimpleTestNotification : INotification
    {
        public string Message { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // Simple test handlers for service registration tests
    public class SimpleTestRequestHandler : IRequestHandler<SimpleTestRequest, string>
    {
        public Task<string> Handle(SimpleTestRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult($"Handled: {request.Message}");
        }
    }

    public class SimpleTestRequestWithoutResponseHandler : IRequestHandler<SimpleTestRequestWithoutResponse>
    {
        public Task Handle(SimpleTestRequestWithoutResponse request, CancellationToken cancellationToken)
        {
            request.Handled = true;
            return Task.CompletedTask;
        }
    }

    public class SimpleTestNotificationHandlerForRegistration : INotificationHandler<SimpleTestNotification>
    {
        public Task Handle(SimpleTestNotification notification, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}