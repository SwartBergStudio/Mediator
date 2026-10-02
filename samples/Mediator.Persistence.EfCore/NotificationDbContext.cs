using Microsoft.EntityFrameworkCore;

namespace Mediator.Samples.Persistence.EfCore;

/// <summary>
/// A stored notification. One row per notification (or per handler retry).
/// </summary>
public sealed class PersistedNotification
{
    public string Id { get; set; } = "";

    /// <summary>When the row was created (used by cleanup).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the row may next be processed. Claiming an item moves this one lease into the future, so only one app
    /// instance processes it at a time; if that instance stops, the item becomes due again when the lease expires.
    /// </summary>
    public DateTime DueAt { get; set; }

    public int AttemptCount { get; set; }
    public string NotificationType { get; set; } = "";
    public string SerializedNotification { get; set; } = "";
    public DateTime WorkItemCreatedAt { get; set; }
    public string? TargetHandlerType { get; set; }
    public string? LastException { get; set; }
}

/// <summary>
/// A small dedicated context for the notifications table. You can also map <see cref="PersistedNotification"/> in your
/// own DbContext with <see cref="ConfigureNotifications"/>.
/// </summary>
public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<PersistedNotification> Notifications => Set<PersistedNotification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureNotifications(modelBuilder);

    public static void ConfigureNotifications(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PersistedNotification>(entity =>
        {
            entity.ToTable("MediatorNotifications");
            entity.HasKey(n => n.Id);
            entity.Property(n => n.Id).HasMaxLength(32);
            entity.Property(n => n.NotificationType).HasMaxLength(1024);
            entity.Property(n => n.TargetHandlerType).HasMaxLength(1024);
            entity.HasIndex(n => n.DueAt);
            entity.HasIndex(n => n.CreatedAt);
        });
    }
}
