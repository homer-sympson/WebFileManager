using System.Security.Cryptography;
using System.Text;
using FileManager.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Data;

/// <summary>Stable codes describing why a session is no longer usable.</summary>
public static class SessionRevocationReasons
{
    /// <summary>Only used when a session is replaced after its owner explicitly ended it (page closed).</summary>
    public const string SupersededByNewSignIn = "session-superseded";

    public const string SignedOut = "signed-out";
    public const string Expired = "session-expired";
    public const string PasswordChanged = "session-password-changed";
    public const string AccountUnusable = "session-account-unusable";
    public const string PageClosed = "session-page-closed";

    /// <summary>An administrator freed a session that its owner could no longer end (crashed browser).</summary>
    public const string ClosedByAdmin = "session-closed-by-admin";

    /// <summary>Not a session state: the sign-in itself was refused because another client is active.</summary>
    public const string AlreadySignedIn = AlreadySignedInException.Code;

    /// <summary>Not a session state: the session is valid but belongs to another tab.</summary>
    public const string TabConflict = "tab-conflict";
}

public sealed record SessionCreated(string Token, DateTime ExpiresUtc, int RevokedSessions);

/// <summary>An active session as shown to administrators.</summary>
public sealed record ActiveSessionInfo(
    int UserId,
    string UserName,
    DateTime CreatedUtc,
    DateTime LastSeenUtc,
    DateTime ExpiresUtc,
    string? RemoteIp,
    string? UserAgent,
    string? ActiveTabId,
    bool PendingPageClose);

/// <summary>Outcome of validating a session cookie.</summary>
public sealed record SessionValidation(AppUser? User, string? ReasonCode, bool TabConflict)
{
    public static readonly SessionValidation None = new(null, null, false);

    public static SessionValidation Valid(AppUser user) => new(user, null, false);

    public static SessionValidation Invalid(string? reasonCode) => new(null, reasonCode, false);

    /// <summary>The session is valid, but another tab of the same browser owns it.</summary>
    public static SessionValidation OtherTab(AppUser user) => new(user, null, true);
}

public interface ISessionService
{
    /// <summary>
    /// Creates the session for the user. Throws <see cref="AlreadySignedInException"/> when the
    /// account still has a live session, so one account can never be used from two browsers.
    /// </summary>
    Task<SessionCreated> CreateAsync(AppUser user, string? remoteIp, string? userAgent, CancellationToken cancellationToken = default);

    Task<SessionValidation> ValidateAsync(string token, string? tabId, CancellationToken cancellationToken = default);

    /// <summary>Signs the session out explicitly.</summary>
    Task RevokeAsync(string token, string reasonCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the browser page was closed. A page reload within the grace window cancels it;
    /// afterwards the session is treated as ended and no longer blocks a new sign-in.
    /// </summary>
    Task MarkPageClosedAsync(string token, string? tabId, CancellationToken cancellationToken = default);

    /// <summary>Revokes live sessions of the user, optionally keeping the caller's own session.</summary>
    Task<int> RevokeAllAsync(int userId, string reasonCode, string? exceptTokenHash = null, CancellationToken cancellationToken = default);

    /// <summary>Live (not revoked, not expired) sessions of this user, if any.</summary>
    Task<ActiveSessionInfo?> GetActiveForUserAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>Every live session in the system, for the administrator overview.</summary>
    Task<IReadOnlyList<ActiveSessionInfo>> GetActiveAsync(CancellationToken cancellationToken = default);
}

public sealed class SessionService : ISessionService
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(60);

    private const int CreateAttempts = 3;

    private readonly IDbContextFactory<FileManagerDbContext> _factory;
    private readonly AuthOptions _options;

    public SessionService(IDbContextFactory<FileManagerDbContext> factory, IOptions<FileManagerOptions> options)
    {
        _factory = factory;
        _options = options.Value.Auth;
    }

    public async Task<SessionCreated> CreateAsync(AppUser user, string? remoteIp, string? userAgent, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < CreateAttempts; attempt++)
        {
            var token = CreateToken();
            var now = DateTime.UtcNow;
            var expires = now.AddMinutes(_options.SessionIdleMinutes);
            var record = new SessionRecord
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = Hash(token),
                CreatedUtc = now,
                LastSeenUtc = now,
                ExpiresUtc = expires,
                RemoteIp = Truncate(remoteIp, 64),
                UserAgent = Truncate(userAgent, 256),
            };

            try
            {
                await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                var live = await db.Sessions
                    .Where(s => s.UserId == user.Id && s.RevokedUtc == null && s.ExpiresUtc > now)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (live.Any(session => !IsPageCloseExpired(session, now)))
                {
                    // Another browser still holds this account: refuse instead of silently kicking it out.
                    throw new AlreadySignedInException(user.UserName, live.Max(s => s.LastSeenUtc));
                }

                // Only abandoned rows remain (expired or page closed): clear them so the unique index
                // accepts the new session.
                var revoked = await db.Sessions
                    .Where(s => s.UserId == user.Id && s.RevokedUtc == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(s => s.RevokedUtc, now)
                            .SetProperty(s => s.RevokedReason, SessionRevocationReasons.PageClosed)
                            .SetProperty(s => s.ActiveTabId, (string?)null),
                        cancellationToken)
                    .ConfigureAwait(false);

                db.Sessions.Add(record);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return new SessionCreated(token, expires, revoked);
            }
            catch (DbUpdateException) when (attempt < CreateAttempts - 1)
            {
                // A concurrent sign-in inserted its row first; the next attempt sees it as live and refuses.
            }
        }

        throw FileManagerException.Conflict("Не удалось создать сессию: выполнен параллельный вход в систему.");
    }

    public async Task<SessionValidation> ValidateAsync(string token, string? tabId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return SessionValidation.None;
        }

        var hash = Hash(token);
        var now = DateTime.UtcNow;

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.Sessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.TokenHash == hash, cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            return SessionValidation.None;
        }

        if (session.RevokedUtc is not null)
        {
            return SessionValidation.Invalid(session.RevokedReason ?? SessionRevocationReasons.SignedOut);
        }

        if (session.ExpiresUtc <= now)
        {
            return SessionValidation.Invalid(SessionRevocationReasons.Expired);
        }

        if (session.User is null)
        {
            return SessionValidation.None;
        }

        // One tab per browser: the first tab claims the session, every other tab is refused.
        if (tabId is not null)
        {
            if (session.ActiveTabId is { } owner)
            {
                if (!string.Equals(owner, tabId, StringComparison.Ordinal))
                {
                    return SessionValidation.OtherTab(session.User);
                }
            }
            else
            {
                session.ActiveTabId = Truncate(tabId, 64);
            }
        }

        if (session.ClosedAtUtc is { } closedAt)
        {
            if (IsPageCloseExpired(session, now))
            {
                session.RevokedUtc = now;
                session.RevokedReason = SessionRevocationReasons.PageClosed;
                session.ActiveTabId = null;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return SessionValidation.Invalid(SessionRevocationReasons.PageClosed);
            }

            // The page is back within the grace window (reload): the close is cancelled.
            session.ClosedAtUtc = null;
        }

        if (now - session.LastSeenUtc >= TouchInterval)
        {
            session.LastSeenUtc = now;
            var sliding = now.AddMinutes(_options.SessionIdleMinutes);
            var absolute = session.CreatedUtc.AddHours(_options.SessionAbsoluteHours);
            session.ExpiresUtc = sliding < absolute ? sliding : absolute;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SessionValidation.Valid(session.User);
    }

    public async Task RevokeAsync(string token, string reasonCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var hash = Hash(token);
        var now = DateTime.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Sessions
            .Where(s => s.TokenHash == hash && s.RevokedUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(s => s.RevokedUtc, now)
                    .SetProperty(s => s.RevokedReason, reasonCode)
                    .SetProperty(s => s.ActiveTabId, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkPageClosedAsync(string token, string? tabId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var hash = Hash(token);
        var now = DateTime.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.Sessions
            .FirstOrDefaultAsync(s => s.TokenHash == hash && s.RevokedUtc == null, cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {
            return;
        }

        // A closing tab that never owned the session must not end it.
        if (tabId is not null && session.ActiveTabId is { } owner && !string.Equals(owner, tabId, StringComparison.Ordinal))
        {
            return;
        }

        session.ClosedAtUtc = now;
        if (session.ActiveTabId is not null && (tabId is null || string.Equals(session.ActiveTabId, tabId, StringComparison.Ordinal)))
        {
            session.ActiveTabId = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RevokeAllAsync(int userId, string reasonCode, string? exceptTokenHash = null, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedUtc == null && (exceptTokenHash == null || s.TokenHash != exceptTokenHash))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(s => s.RevokedUtc, now)
                    .SetProperty(s => s.RevokedReason, reasonCode)
                    .SetProperty(s => s.ActiveTabId, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ActiveSessionInfo?> GetActiveForUserAsync(int userId, CancellationToken cancellationToken = default)
    {
        var live = await LoadActiveAsync(session => session.UserId == userId, cancellationToken).ConfigureAwait(false);
        return live.FirstOrDefault();
    }

    public async Task<IReadOnlyList<ActiveSessionInfo>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var live = await LoadActiveAsync(static _ => true, cancellationToken).ConfigureAwait(false);
        return live.OrderBy(session => session.UserName, StringComparer.Ordinal).ToArray();
    }

    private async Task<List<ActiveSessionInfo>> LoadActiveAsync(
        System.Linq.Expressions.Expression<Func<SessionRecord, bool>> filter,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var sessions = await db.Sessions
            .Include(s => s.User)
            .Where(s => s.RevokedUtc == null && s.ExpiresUtc > now)
            .Where(filter)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A session whose owner closed the page and never came back is already over for sign-in purposes.
        return sessions
            .Where(session => session.User is not null && !IsPageCloseExpired(session, now))
            .Select(session => new ActiveSessionInfo(
                session.UserId,
                session.User!.UserName,
                session.CreatedUtc,
                session.LastSeenUtc,
                session.ExpiresUtc,
                session.RemoteIp,
                session.UserAgent,
                session.ActiveTabId,
                session.ClosedAtUtc is not null))
            .ToList();
    }

    public static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
    }
    private bool IsPageCloseExpired(SessionRecord session, DateTime now) =>
        session.ClosedAtUtc is { } closedAt &&
        now - closedAt >= TimeSpan.FromSeconds(Math.Max(0, _options.PageCloseGraceSeconds));

    private static string CreateToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Base64UrlEncode(buffer);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
