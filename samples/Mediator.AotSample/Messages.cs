using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mediator;

[assembly: MediatorPipelineBehaviors(typeof(Mediator.AotSample.CountingBehavior<,>), typeof(Mediator.AotSample.AuditCommands<>))]

namespace Mediator.AotSample;

public sealed record GetGreeting(string Name) : IRequest<string>;

public sealed class GetGreetingHandler : IRequestHandler<GetGreeting, string>
{
    public Task<string> Handle(GetGreeting request, CancellationToken cancellationToken)
        => Task.FromResult($"Hello, {request.Name}!");
}

public sealed record GetTotal(int[] Values) : IRequest<int>;

public sealed class GetTotalHandler : IRequestHandler<GetTotal, int>
{
    public Task<int> Handle(GetTotal request, CancellationToken cancellationToken)
        => Task.FromResult(request.Values.Sum());
}

public sealed record RecordVisit(string Page) : IRequest;

public sealed class RecordVisitHandler(Tracker tracker) : IRequestHandler<RecordVisit>
{
    public Task Handle(RecordVisit request, CancellationToken cancellationToken)
    {
        tracker.Add($"visit:{request.Page}");
        return Task.CompletedTask;
    }
}

public sealed record CountTo(int Max) : IStreamRequest<int>;

public sealed class CountToHandler : IStreamRequestHandler<CountTo, int>
{
    public async IAsyncEnumerable<int> Handle(CountTo request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Max; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

public sealed class OrderPlaced : INotification
{
    public string OrderId { get; set; } = string.Empty;
}

public sealed class EmailOnOrderPlaced(Tracker tracker) : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        tracker.Add($"email:{notification.OrderId}");
        return Task.CompletedTask;
    }
}

public sealed class AuditOnOrderPlaced(Tracker tracker) : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        tracker.Add($"audit:{notification.OrderId}");
        return Task.CompletedTask;
    }
}

/// <summary>Pipeline behavior registered explicitly (behaviors are not discovered automatically).</summary>
public sealed class UppercaseGreeting : IPipelineBehavior<GetGreeting, string>
{
    public async Task<string> Handle(GetGreeting request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        => (await next()).ToUpperInvariant();
}

public sealed class Tracker
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _events = new();
    public void Add(string value) => _events.Enqueue(value);
    public IReadOnlyCollection<string> Events => _events;
}

/// <summary>Source-generated JSON metadata for persisted notifications (required for persistence under Native AOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(ReportGenerated))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;

/// <summary>Open generic behavior declared with [assembly: MediatorPipelineBehaviors]; the generator closes it per request.</summary>
public sealed class CountingBehavior<TRequest, TResponse>(Tracker tracker) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        tracker.Add($"behavior:{typeof(TRequest).Name}");
        return next();
    }
}

/// <summary>Declared behavior for requests without a response (commands).</summary>
public sealed class AuditCommands<TRequest>(Tracker tracker) : IPipelineBehavior<TRequest>
    where TRequest : IRequest
{
    public async Task Handle(TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken)
    {
        tracker.Add($"command-behavior:{typeof(TRequest).Name}");
        await next();
    }
}

/// <summary>Scoped per caller, like a per-user session.</summary>
public sealed class UserSession
{
    public string? UserId { get; set; }
}

public sealed class InvoiceApproved : INotification
{
    public string InvoiceId { get; set; } = string.Empty;
}

public sealed class UpdateInvoiceReadModel(Tracker tracker, UserSession session) : INotificationHandler<InvoiceApproved>
{
    public async Task Handle(InvoiceApproved notification, CancellationToken cancellationToken)
    {
        await Task.Yield();
        tracker.Add($"approved:{notification.InvoiceId}:by:{session.UserId}");
    }
}

/// <summary>Closed stream behavior: discovered and registered by the source generator.</summary>
public sealed class DoubleNumbers : IStreamPipelineBehavior<CountTo, int>
{
    public async IAsyncEnumerable<int> Handle(CountTo request, StreamHandlerDelegate<int> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in next().WithCancellation(cancellationToken))
        {
            yield return item * 2;
        }
    }
}

public sealed record Explode : IRequest<string>;

public sealed class ExplodeHandler : IRequestHandler<Explode, string>
{
    public Task<string> Handle(Explode request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("boom");
}

public sealed class ReportGenerated : INotification
{
    public string ReportId { get; set; } = string.Empty;
}

public sealed class ReportGeneratedHandler(Tracker tracker) : INotificationHandler<ReportGenerated>
{
    public Task Handle(ReportGenerated notification, CancellationToken cancellationToken)
    {
        tracker.Add($"report:{notification.ReportId}");
        return Task.CompletedTask;
    }
}
