using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Mediator.Tests
{
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
}
