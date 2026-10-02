using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mediator;

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
internal sealed partial class SampleJsonContext : JsonSerializerContext;
