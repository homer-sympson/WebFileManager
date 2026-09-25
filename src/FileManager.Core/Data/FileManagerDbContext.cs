using Microsoft.EntityFrameworkCore;

namespace FileManager.Core.Data;

public class AppUser
{
    public int Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public uint Uid { get; set; }

    public bool IsAdmin { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastLoginUtc { get; set; }

    public ICollection<SessionRecord> Sessions { get; set; } = [];
}

public class SessionRecord
{
    public Guid Id { get; set; }

    public int UserId { get; set; }

    public AppUser? User { get; set; }

    /// <summary>SHA-256 of the opaque cookie value. The raw token is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }

    public DateTime ExpiresUtc { get; set; }

    public DateTime? RevokedUtc { get; set; }

    /// <summary>Stable code explaining why the session ended (single session policy, logout, expiry...).</summary>
    public string? RevokedReason { get; set; }

    /// <summary>Set when the browser reported that the page was closed; a reload within the grace window clears it.</summary>
    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>Tab that currently owns the session; every other tab of the same browser is rejected.</summary>
    public string? ActiveTabId { get; set; }

    public string? RemoteIp { get; set; }

    public string? UserAgent { get; set; }
}

public class NavigationHistoryEntry
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public AppUser? User { get; set; }

    public string Path { get; set; } = string.Empty;

    public DateTime VisitedUtc { get; set; }

    public int VisitCount { get; set; }
}

public class AuditEntry
{
    public long Id { get; set; }

    public int? ActorUserId { get; set; }

    public string? ActorUserName { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? TargetPath { get; set; }

    public string? Details { get; set; }

    public DateTime CreatedUtc { get; set; }

    public string? RemoteIp { get; set; }
}

public class FileManagerDbContext : DbContext
{
    public FileManagerDbContext(DbContextOptions<FileManagerDbContext> options)
        : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();

    public DbSet<NavigationHistoryEntry> NavigationHistory => Set<NavigationHistoryEntry>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserName).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.UserName).IsUnique();
            entity.HasIndex(e => e.Uid);
        });

        modelBuilder.Entity<SessionRecord>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TokenHash).HasMaxLength(128).IsRequired();
            entity.Property(e => e.RevokedReason).HasMaxLength(64);
            entity.Property(e => e.ActiveTabId).HasMaxLength(64);
            entity.HasIndex(e => e.TokenHash).IsUnique();

            // At most one live session per user, enforced by the database itself so two concurrent
            // sign-ins can never both stay active.
            entity.HasIndex(e => e.UserId)
                .IsUnique()
                .HasFilter("\"RevokedUtc\" IS NULL");

            entity.HasOne(e => e.User)
                .WithMany(u => u.Sessions)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NavigationHistoryEntry>(entity =>
        {
            entity.ToTable("navigation_history");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Path).HasMaxLength(4096).IsRequired();
            entity.HasIndex(e => new { e.UserId, e.VisitedUtc });
            entity.HasIndex(e => new { e.UserId, e.Path }).IsUnique();
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ActorUserName).HasMaxLength(64);
            entity.Property(e => e.TargetPath).HasMaxLength(4096);
            entity.HasIndex(e => e.CreatedUtc);
        });
    }
}
