using Mediator.Persistence;
using Mediator.Samples.Persistence.EfCore;
using Mediator.Samples.Persistence.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using StackExchange.Redis;
using Xunit;

namespace Mediator.Samples.Persistence.Tests;

/// <summary>Runs the contract against EF Core with SQLite (a temporary database file per test).</summary>
public sealed class EfCorePersistenceTests : PersistenceContractTests
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mediator-efcore-{Guid.NewGuid():N}.db");
    private PooledDbContextFactory<NotificationDbContext>? _factory;

    public override async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<NotificationDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        _factory = new PooledDbContextFactory<NotificationDbContext>(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public override Task DisposeAsync()
    {
        try { File.Delete(_databasePath); } catch (IOException) { }
        return Task.CompletedTask;
    }

    protected override INotificationPersistence CreateStore(TimeSpan lease)
        => new EfCoreNotificationPersistence(_factory!, new EfCoreNotificationPersistenceOptions { LeaseDuration = lease });
}

/// <summary>
/// Runs the contract against Redis. Uses REDIS_CONNECTION (default localhost:6379); skipped when no server is reachable.
/// </summary>
public sealed class RedisPersistenceTests : PersistenceContractTests
{
    private static readonly Lazy<IConnectionMultiplexer?> s_redis = new(() =>
    {
        try
        {
            var connection = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";
            return ConnectionMultiplexer.Connect($"{connection},connectTimeout=2000,abortConnect=true");
        }
        catch (RedisConnectionException)
        {
            return null;
        }
    });

    private readonly string _prefix = $"mediator-tests:{Guid.NewGuid():N}:";

    protected override void EnsureAvailable()
        => Skip.If(s_redis.Value is null, "No Redis server reachable (set REDIS_CONNECTION).");

    protected override INotificationPersistence CreateStore(TimeSpan lease)
        => new RedisNotificationPersistence(s_redis.Value!, new RedisNotificationPersistenceOptions { KeyPrefix = _prefix, LeaseDuration = lease });

    public override async Task DisposeAsync()
    {
        if (s_redis.Value is null) return;
        var server = s_redis.Value.GetServers()[0];
        var db = s_redis.Value.GetDatabase();
        await foreach (var key in server.KeysAsync(pattern: _prefix + "*"))
        {
            await db.KeyDeleteAsync(key);
        }
    }
}
