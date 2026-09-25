using Microsoft.EntityFrameworkCore;

namespace FileManager.Core.Data;

public sealed record HistoryItem(string Path, DateTime VisitedUtc, int VisitCount);

public interface IHistoryService
{
    Task RecordAsync(int userId, string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoryItem>> GetAsync(int userId, int limit, CancellationToken cancellationToken = default);

    Task ClearAsync(int userId, CancellationToken cancellationToken = default);
}

public sealed class HistoryService : IHistoryService
{
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<FileManagerDbContext> _factory;

    public HistoryService(IDbContextFactory<FileManagerDbContext> factory)
    {
        _factory = factory;
    }

    public async Task RecordAsync(int userId, string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var trimmed = path.Length > 4096 ? path[..4096] : path;
        var now = DateTime.UtcNow;

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await db.NavigationHistory
            .FirstOrDefaultAsync(h => h.UserId == userId && h.Path == trimmed, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            db.NavigationHistory.Add(new NavigationHistoryEntry
            {
                UserId = userId,
                Path = trimmed,
                VisitedUtc = now,
                VisitCount = 1,
            });
        }
        else if (now - entry.VisitedUtc < CoalesceWindow)
        {
            return; // repeated refresh of the same folder should not spam the history
        }
        else
        {
            entry.VisitedUtc = now;
            entry.VisitCount++;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Concurrent insert for the same (user, path) pair; the history entry already exists.
        }
    }

    public async Task<IReadOnlyList<HistoryItem>> GetAsync(int userId, int limit, CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var items = await db.NavigationHistory
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.VisitedUtc)
            .Take(take)
            .Select(h => new HistoryItem(h.Path, h.VisitedUtc, h.VisitCount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return items;
    }

    public async Task ClearAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entries = await db.NavigationHistory.Where(h => h.UserId == userId).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.NavigationHistory.RemoveRange(entries);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public interface IAuditService
{
    Task WriteAsync(
        int? actorUserId,
        string? actorUserName,
        string action,
        string? targetPath = null,
        string? details = null,
        string? remoteIp = null,
        CancellationToken cancellationToken = default);
}

public sealed class AuditService : IAuditService
{
    private readonly IDbContextFactory<FileManagerDbContext> _factory;

    public AuditService(IDbContextFactory<FileManagerDbContext> factory)
    {
        _factory = factory;
    }

    public async Task WriteAsync(
        int? actorUserId,
        string? actorUserName,
        string action,
        string? targetPath = null,
        string? details = null,
        string? remoteIp = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.AuditLog.Add(new AuditEntry
            {
                ActorUserId = actorUserId,
                ActorUserName = actorUserName,
                Action = action,
                TargetPath = Truncate(targetPath, 4096),
                Details = details,
                CreatedUtc = DateTime.UtcNow,
                RemoteIp = Truncate(remoteIp, 64),
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Auditing must never break the request it describes.
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
