using Mediator;
using Mediator.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mediator.Samples.Persistence.EfCore;

/// <summary>
/// Options for <see cref="EfCoreNotificationPersistence"/>.
/// </summary>
public sealed class EfCoreNotificationPersistenceOptions
{
    /// <summary>
    /// How long an item is reserved for the instance that published or claimed it. If that instance stops before
    /// completing the item, another instance picks it up after this time. Must be longer than your slowest handler.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Sample <see cref="INotificationPersistence"/> using EF Core, safe for several app instances sharing one database.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Uses <see cref="IDbContextFactory{TContext}"/>: persistence is a singleton and must not hold a scoped DbContext.</item>
/// <item>A newly published item is due only after the lease: the publishing instance handles it in memory, and other
/// instances only take over if it never completes.</item>
/// <item><see cref="GetPendingAsync"/> claims each due item with a conditional update (<c>WHERE Id = .. AND DueAt = ..</c>),
/// so when two instances race for an item exactly one wins.</item>
/// <item>Retry items are inserted together with their retry time (<see cref="INotificationRetryPersistence"/>).</item>
/// </list>
/// This stores notifications in its own short transactions; it is not a transactional outbox (the notification is not
/// saved in the same transaction as your business data).
/// <code>
/// services.AddDbContextFactory&lt;NotificationDbContext&gt;(o =&gt; o.UseSqlServer(connectionString));
/// services.AddSingleton&lt;INotificationPersistence, EfCoreNotificationPersistence&gt;();
/// services.AddMediator(o =&gt; o.EnablePersistence = true, typeof(Program).Assembly);
/// </code>
/// Create the table with a migration, or <c>context.Database.EnsureCreated()</c> for a dedicated database.
/// </remarks>
public sealed class EfCoreNotificationPersistence : INotificationPersistence, INotificationRetryPersistence
{
    private readonly IDbContextFactory<NotificationDbContext> _contextFactory;
    private readonly EfCoreNotificationPersistenceOptions _options;

    public EfCoreNotificationPersistence(IDbContextFactory<NotificationDbContext> contextFactory)
        : this(contextFactory, new EfCoreNotificationPersistenceOptions())
    {
    }

    public EfCoreNotificationPersistence(IDbContextFactory<NotificationDbContext> contextFactory, EfCoreNotificationPersistenceOptions options)
    {
        _contextFactory = contextFactory;
        _options = options;
    }

    public Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default)
        => InsertAsync(workItem, attemptCount: 0, dueAt: DateTime.UtcNow + _options.LeaseDuration, exception: null, cancellationToken);

    public Task<string> PersistForRetryAsync(NotificationWorkItem workItem, int attemptCount, DateTime retryAfter, Exception? exception, CancellationToken cancellationToken = default)
        => InsertAsync(workItem, attemptCount, retryAfter, exception, cancellationToken);

    public async Task<IEnumerable<PersistedNotificationWorkItem>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var leaseUntil = now + _options.LeaseDuration;

        var candidates = await db.Notifications
            .AsNoTracking()
            .Where(n => n.DueAt <= now)
            .OrderBy(n => n.DueAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var claimed = new List<PersistedNotificationWorkItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // Claim: only succeeds if no other instance moved the due time since we read it.
            var updated = await db.Notifications
                .Where(n => n.Id == candidate.Id && n.DueAt == candidate.DueAt)
                .ExecuteUpdateAsync(set => set.SetProperty(n => n.DueAt, leaseUntil), cancellationToken);
            if (updated != 1)
                continue;

            var type = Type.GetType(candidate.NotificationType, throwOnError: false);
            if (type is null)
            {
                // The type no longer exists: drop the row so it isn't claimed forever.
                await CompleteAsync(candidate.Id, cancellationToken);
                continue;
            }

            claimed.Add(new PersistedNotificationWorkItem
            {
                Id = candidate.Id,
                CreatedAt = candidate.CreatedAt,
                AttemptCount = candidate.AttemptCount,
                WorkItem = new NotificationWorkItem(null, type, candidate.WorkItemCreatedAt, candidate.SerializedNotification)
                {
                    TargetHandlerType = candidate.TargetHandlerType,
                },
            });
        }
        return claimed;
    }

    public async Task CompleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Notifications.Where(n => n.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task FailAsync(string id, Exception exception, DateTime? retryAfter = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var dueAt = retryAfter ?? DateTime.UtcNow;
        var lastException = exception.ToString();
        await db.Notifications
            .Where(n => n.Id == id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(n => n.AttemptCount, n => n.AttemptCount + 1)
                .SetProperty(n => n.DueAt, dueAt)
                .SetProperty(n => n.LastException, lastException), cancellationToken);
    }

    public async Task CleanupAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Notifications.Where(n => n.CreatedAt < olderThan).ExecuteDeleteAsync(cancellationToken);
    }

    public void Dispose()
    {
        // Contexts are created and disposed per operation.
    }

    private async Task<string> InsertAsync(NotificationWorkItem workItem, int attemptCount, DateTime dueAt, Exception? exception, CancellationToken cancellationToken)
    {
        if (workItem.NotificationType is null || string.IsNullOrEmpty(workItem.SerializedNotification))
            throw new ArgumentException("NotificationType and SerializedNotification are required.", nameof(workItem));

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = new PersistedNotification
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            DueAt = dueAt,
            AttemptCount = attemptCount,
            NotificationType = workItem.NotificationType.AssemblyQualifiedName!,
            SerializedNotification = workItem.SerializedNotification,
            WorkItemCreatedAt = workItem.CreatedAt,
            TargetHandlerType = workItem.TargetHandlerType,
            LastException = exception?.ToString(),
        };
        db.Notifications.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row.Id;
    }
}
