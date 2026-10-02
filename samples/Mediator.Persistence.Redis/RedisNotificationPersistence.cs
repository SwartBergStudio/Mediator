using System.Text.Json;
using System.Text.Json.Serialization;
using Mediator;
using Mediator.Persistence;
using StackExchange.Redis;

namespace Mediator.Samples.Persistence.Redis;

/// <summary>
/// Options for <see cref="RedisNotificationPersistence"/>.
/// </summary>
public sealed class RedisNotificationPersistenceOptions
{
    /// <summary>Prefix for all keys, so several apps can share one Redis database.</summary>
    public string KeyPrefix { get; set; } = "mediator:notifications:";

    /// <summary>
    /// How long an item is reserved for the instance that published or claimed it. If that instance stops before
    /// completing the item, another instance picks it up after this time. Must be longer than your slowest handler.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Sample <see cref="INotificationPersistence"/> for Redis, safe for several app instances sharing one Redis.
/// </summary>
/// <remarks>
/// Layout: each item is a JSON string at <c>{prefix}item:{id}</c>. A sorted set <c>{prefix}due</c> holds every
/// item's id scored by when it may next be processed. A second sorted set <c>{prefix}created</c> scores ids by creation
/// time for cleanup.
/// <list type="bullet">
/// <item>A newly published item is due only after <see cref="RedisNotificationPersistenceOptions.LeaseDuration"/>: the
/// publishing instance handles it in memory, and other instances only take over if it never completes.</item>
/// <item><see cref="GetPendingAsync"/> claims due items atomically (a Lua script moves their due time one lease into the
/// future), so two instances never receive the same item.</item>
/// <item>Retry items are written together with their retry time (<see cref="INotificationRetryPersistence"/>).</item>
/// </list>
/// Register it before calling AddMediator:
/// <code>
/// services.AddSingleton&lt;IConnectionMultiplexer&gt;(ConnectionMultiplexer.Connect("localhost:6379"));
/// services.AddSingleton&lt;INotificationPersistence, RedisNotificationPersistence&gt;();
/// services.AddMediator(o =&gt; o.EnablePersistence = true, typeof(Program).Assembly);
/// </code>
/// </remarks>
public sealed class RedisNotificationPersistence : INotificationPersistence, INotificationRetryPersistence
{
    // Atomically claims up to ARGV[2] items due at ARGV[1] by moving them to ARGV[3] (now + lease); returns their ids.
    private const string ClaimScript = """
        local ids = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, tonumber(ARGV[2]))
        for _, id in ipairs(ids) do
            redis.call('ZADD', KEYS[1], ARGV[3], id)
        end
        return ids
        """;

    private readonly IDatabase _db;
    private readonly RedisNotificationPersistenceOptions _options;
    private readonly RedisKey _dueKey;
    private readonly RedisKey _createdKey;

    public RedisNotificationPersistence(IConnectionMultiplexer redis)
        : this(redis, new RedisNotificationPersistenceOptions())
    {
    }

    public RedisNotificationPersistence(IConnectionMultiplexer redis, RedisNotificationPersistenceOptions options)
    {
        _db = redis.GetDatabase();
        _options = options;
        _dueKey = options.KeyPrefix + "due";
        _createdKey = options.KeyPrefix + "created";
    }

    public Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default)
        => StoreAsync(workItem, attemptCount: 0, dueAt: DateTime.UtcNow + _options.LeaseDuration, exception: null);

    public Task<string> PersistForRetryAsync(NotificationWorkItem workItem, int attemptCount, DateTime retryAfter, Exception? exception, CancellationToken cancellationToken = default)
        => StoreAsync(workItem, attemptCount, retryAfter, exception);

    public async Task<IEnumerable<PersistedNotificationWorkItem>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var claimed = (RedisResult[]?)await _db.ScriptEvaluateAsync(
            ClaimScript,
            new[] { _dueKey },
            new RedisValue[] { ToScore(now), batchSize, ToScore(now + _options.LeaseDuration) });

        if (claimed is null || claimed.Length == 0)
            return Array.Empty<PersistedNotificationWorkItem>();

        var ids = claimed.Select(r => (string)r!).ToArray();
        var values = await _db.StringGetAsync(ids.Select(ItemKey).ToArray());

        var items = new List<PersistedNotificationWorkItem>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            if (values[i].IsNullOrEmpty || ToWorkItem(ids[i], values[i]!) is not { } item)
            {
                // Missing or unreadable: drop it so it isn't claimed forever.
                await RemoveAsync(ids[i]);
                continue;
            }
            items.Add(item);
        }
        return items;
    }

    public Task CompleteAsync(string id, CancellationToken cancellationToken = default) => RemoveAsync(id);

    public async Task FailAsync(string id, Exception exception, DateTime? retryAfter = null, CancellationToken cancellationToken = default)
    {
        var json = await _db.StringGetAsync(ItemKey(id));
        if (json.IsNullOrEmpty)
            return;

        var record = JsonSerializer.Deserialize((string)json!, RedisRecordContext.Default.StoredNotification);
        if (record is null)
        {
            await RemoveAsync(id);
            return;
        }

        record.AttemptCount++;
        record.LastException = exception.ToString();

        var transaction = _db.CreateTransaction();
        _ = transaction.StringSetAsync(ItemKey(id), JsonSerializer.Serialize(record, RedisRecordContext.Default.StoredNotification));
        _ = transaction.SortedSetAddAsync(_dueKey, id, ToScore(retryAfter ?? DateTime.UtcNow));
        await transaction.ExecuteAsync();
    }

    public async Task CleanupAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        var expired = await _db.SortedSetRangeByScoreAsync(_createdKey, double.NegativeInfinity, ToScore(olderThan));
        foreach (var id in expired)
        {
            await RemoveAsync(id!);
        }
    }

    public void Dispose()
    {
        // The connection is owned by the DI container (IConnectionMultiplexer singleton).
    }

    private async Task<string> StoreAsync(NotificationWorkItem workItem, int attemptCount, DateTime dueAt, Exception? exception)
    {
        if (workItem.NotificationType is null || string.IsNullOrEmpty(workItem.SerializedNotification))
            throw new ArgumentException("NotificationType and SerializedNotification are required.", nameof(workItem));

        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var record = new StoredNotification
        {
            Id = id,
            CreatedAt = now,
            AttemptCount = attemptCount,
            NotificationType = workItem.NotificationType.AssemblyQualifiedName!,
            SerializedNotification = workItem.SerializedNotification,
            WorkItemCreatedAt = workItem.CreatedAt,
            TargetHandlerType = workItem.TargetHandlerType,
            LastException = exception?.ToString(),
        };

        // One transaction: the item and its due time become visible together.
        var transaction = _db.CreateTransaction();
        _ = transaction.StringSetAsync(ItemKey(id), JsonSerializer.Serialize(record, RedisRecordContext.Default.StoredNotification));
        _ = transaction.SortedSetAddAsync(_dueKey, id, ToScore(dueAt));
        _ = transaction.SortedSetAddAsync(_createdKey, id, ToScore(now));
        await transaction.ExecuteAsync();
        return id;
    }

    private async Task RemoveAsync(string id)
    {
        var transaction = _db.CreateTransaction();
        _ = transaction.KeyDeleteAsync(ItemKey(id));
        _ = transaction.SortedSetRemoveAsync(_dueKey, id);
        _ = transaction.SortedSetRemoveAsync(_createdKey, id);
        await transaction.ExecuteAsync();
    }

    private static PersistedNotificationWorkItem? ToWorkItem(string id, string json)
    {
        var record = JsonSerializer.Deserialize(json, RedisRecordContext.Default.StoredNotification);
        var type = record is null ? null : Type.GetType(record.NotificationType, throwOnError: false);
        if (record is null || type is null)
            return null;

        return new PersistedNotificationWorkItem
        {
            Id = id,
            CreatedAt = record.CreatedAt,
            AttemptCount = record.AttemptCount,
            WorkItem = new NotificationWorkItem(null, type, record.WorkItemCreatedAt, record.SerializedNotification)
            {
                TargetHandlerType = record.TargetHandlerType,
            },
        };
    }

    private RedisKey ItemKey(string id) => _options.KeyPrefix + "item:" + id;

    private static double ToScore(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}

/// <summary>The JSON stored per notification.</summary>
public sealed class StoredNotification
{
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int AttemptCount { get; set; }
    public string NotificationType { get; set; } = "";
    public string SerializedNotification { get; set; } = "";
    public DateTime WorkItemCreatedAt { get; set; }
    public string? TargetHandlerType { get; set; }
    public string? LastException { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredNotification))]
internal sealed partial class RedisRecordContext : JsonSerializerContext;
