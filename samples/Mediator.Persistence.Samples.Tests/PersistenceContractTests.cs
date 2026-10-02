using System.Collections.Concurrent;
using FluentAssertions;
using Mediator.Persistence;
using Mediator.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Mediator.Samples.Persistence.Tests;

/// <summary>
/// Behavior every persistence sample must have, run against each store. "Two instances" are two store objects over the
/// same backend, like two app instances sharing a database.
/// </summary>
public abstract class PersistenceContractTests : IAsyncLifetime
{
    /// <summary>Creates a store over this test's backend, with the given lease.</summary>
    protected abstract INotificationPersistence CreateStore(TimeSpan lease);

    /// <summary>Throws a skip exception when the backend isn't available (e.g. no Redis server).</summary>
    protected virtual void EnsureAvailable() { }

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task New_item_is_not_due_until_its_lease_expires()
    {
        EnsureAvailable();
        var store = CreateStore(TimeSpan.FromMilliseconds(300));

        var id = await store.PersistAsync(WorkItem());

        (await store.GetPendingAsync()).Should().BeEmpty("the publishing instance is still handling it in memory");
        await Task.Delay(400);
        (await store.GetPendingAsync()).Should().ContainSingle(i => i.Id == id && i.AttemptCount == 0);
    }

    [SkippableFact]
    public async Task Retry_item_is_due_at_its_retry_time_with_its_target_handler()
    {
        EnsureAvailable();
        var store = (INotificationRetryPersistence)CreateStore(TimeSpan.FromMinutes(5));
        var item = WorkItem() with { TargetHandlerType = "App.SendEmail, App" };

        var later = await store.PersistForRetryAsync(item, 2, DateTime.UtcNow.AddMinutes(1), new Exception("x"));
        var now = await store.PersistForRetryAsync(item, 1, DateTime.UtcNow.AddSeconds(-1), new Exception("x"));

        var pending = (await ((INotificationPersistence)store).GetPendingAsync()).ToList();
        pending.Should().ContainSingle().Which.Id.Should().Be(now);
        pending[0].AttemptCount.Should().Be(1);
        pending[0].WorkItem.TargetHandlerType.Should().Be("App.SendEmail, App");
        pending[0].WorkItem.NotificationType.Should().Be(typeof(SampleEvent));
        pending.Should().NotContain(p => p.Id == later);
    }

    [SkippableFact]
    public async Task Two_instances_never_claim_the_same_item()
    {
        EnsureAvailable();
        var first = CreateStore(TimeSpan.Zero);
        var second = CreateStore(TimeSpan.FromMinutes(5));
        var ids = new List<string>();
        for (var i = 0; i < 40; i++) ids.Add(await first.PersistAsync(WorkItem()));

        // Both instances claim concurrently; leases are long, so nothing is handed out twice.
        var claimers = new[] { CreateStore(TimeSpan.FromMinutes(5)), second };
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => claimers[i % 2].GetPendingAsync(batchSize: 10)));

        var claimed = results.SelectMany(r => r).Select(r => r.Id).ToList();
        claimed.Should().OnlyHaveUniqueItems();
        claimed.Should().BeEquivalentTo(ids);
    }

    [SkippableFact]
    public async Task Claimed_item_is_not_returned_again_until_its_lease_expires()
    {
        EnsureAvailable();
        var store = CreateStore(TimeSpan.FromMilliseconds(300));
        var id = await ((INotificationRetryPersistence)store).PersistForRetryAsync(WorkItem(), 1, DateTime.UtcNow.AddSeconds(-1), null);

        (await store.GetPendingAsync()).Should().ContainSingle(i => i.Id == id);
        (await store.GetPendingAsync()).Should().BeEmpty("it is leased to the instance that claimed it");
        await Task.Delay(400);
        (await store.GetPendingAsync()).Should().ContainSingle(i => i.Id == id, "the lease expired, so the claimer is presumed gone");
    }

    [SkippableFact]
    public async Task Fail_increments_attempts_and_reschedules_and_complete_removes()
    {
        EnsureAvailable();
        var store = CreateStore(TimeSpan.Zero);
        var id = await store.PersistAsync(WorkItem());

        await store.FailAsync(id, new InvalidOperationException("boom"), DateTime.UtcNow.AddMinutes(1));
        (await store.GetPendingAsync()).Should().BeEmpty("it is scheduled for later");

        await store.FailAsync(id, new InvalidOperationException("boom"), DateTime.UtcNow.AddSeconds(-1));
        (await store.GetPendingAsync()).Should().ContainSingle(i => i.Id == id).Which.AttemptCount.Should().Be(2);

        await store.CompleteAsync(id);
        await Task.Delay(10);
        (await store.GetPendingAsync()).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Cleanup_removes_items_created_before_the_cutoff()
    {
        EnsureAvailable();
        var store = CreateStore(TimeSpan.Zero);
        await store.PersistAsync(WorkItem());

        await store.CleanupAsync(DateTime.UtcNow.AddSeconds(1));

        (await store.GetPendingAsync()).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Mediator_retries_only_the_failed_handler_through_this_store()
    {
        EnsureAvailable();
        var probe = new Probe();
        probe.FailuresLeft["Flaky"] = 1;

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Critical));
        services.AddSingleton(probe);
        services.AddSingleton(CreateStore(TimeSpan.FromSeconds(30)));
        services.AddSingleton<INotificationSerializer>(new JsonNotificationSerializer(SampleJsonContext.Default.Options));
        services.AddTransient<INotificationHandler<SampleEvent>, StableHandler>();
        services.AddTransient<INotificationHandler<SampleEvent>, FlakyHandler>();
        services.AddMediator(options =>
        {
            options.EnablePersistence = true;
            options.NotificationWorkerCount = 1;
            options.ProcessingInterval = TimeSpan.FromMilliseconds(50);
            options.InitialRetryDelay = TimeSpan.FromMilliseconds(50);
            options.MaxRetryAttempts = 3;
        });
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IMediator>().Publish(new SampleEvent { Name = "order-42" });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (probe.Count("Flaky") < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(300);

        probe.Count("Stable").Should().Be(1, "it succeeded and must not run again");
        probe.Count("Flaky").Should().Be(2, "it failed once and was retried alone");
        probe.Names.Should().OnlyContain(n => n == "order-42", "retries are deserialized from the store");
    }

    private static NotificationWorkItem WorkItem()
        => new(null, typeof(SampleEvent), DateTime.UtcNow, "{\"name\":\"x\"}");

    public sealed class Probe
    {
        private readonly ConcurrentDictionary<string, int> _calls = new();
        public ConcurrentDictionary<string, int> FailuresLeft { get; } = new();
        public ConcurrentBag<string> Names { get; } = new();

        public int Count(string handler) => _calls.TryGetValue(handler, out var c) ? c : 0;

        public Task Run(string handler, SampleEvent notification)
        {
            _calls.AddOrUpdate(handler, 1, (_, c) => c + 1);
            Names.Add(notification.Name);
            var fail = false;
            FailuresLeft.AddOrUpdate(handler, 0, (_, left) => { fail = left > 0; return Math.Max(0, left - 1); });
            return fail ? Task.FromException(new InvalidOperationException($"{handler} failed")) : Task.CompletedTask;
        }
    }

    public sealed class StableHandler(Probe probe) : INotificationHandler<SampleEvent>
    {
        public Task Handle(SampleEvent notification, CancellationToken cancellationToken) => probe.Run("Stable", notification);
    }

    public sealed class FlakyHandler(Probe probe) : INotificationHandler<SampleEvent>
    {
        public Task Handle(SampleEvent notification, CancellationToken cancellationToken) => probe.Run("Flaky", notification);
    }
}

public sealed class SampleEvent : INotification
{
    public string Name { get; set; } = "";
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(SampleEvent))]
internal sealed partial class SampleJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
