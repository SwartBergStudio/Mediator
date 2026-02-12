namespace Mediator.Tests
{
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
