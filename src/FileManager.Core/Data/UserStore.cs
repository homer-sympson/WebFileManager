using Microsoft.EntityFrameworkCore;

namespace FileManager.Core.Data;

public interface IUserStore
{
    Task<AppUser> GetOrCreateAsync(string userName, uint uid, bool isAdmin, CancellationToken cancellationToken = default);

    Task<AppUser?> FindAsync(string userName, CancellationToken cancellationToken = default);
}

public sealed class UserStore : IUserStore
{
    private readonly IDbContextFactory<FileManagerDbContext> _factory;

    public UserStore(IDbContextFactory<FileManagerDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<AppUser> GetOrCreateAsync(string userName, uint uid, bool isAdmin, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        if (user is null)
        {
            user = new AppUser
            {
                UserName = userName,
                Uid = uid,
                IsAdmin = isAdmin,
                FirstSeenUtc = now,
                LastLoginUtc = now,
            };
            db.Users.Add(user);
        }
        else
        {
            user.LastLoginUtc = now;
            user.Uid = uid;
            user.IsAdmin = isAdmin;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }

    public async Task<AppUser?> FindAsync(string userName, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users.FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken).ConfigureAwait(false);
    }
}
