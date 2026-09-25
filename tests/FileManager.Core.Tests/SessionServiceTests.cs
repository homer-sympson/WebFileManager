using FileManager.Core.Configuration;
using FileManager.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Tests;

/// <summary>
/// Verifies the strict single-client policy: a second browser is refused (not silently substituted),
/// a closed page ends the session after a short grace window, and only one tab owns a session.
/// </summary>
public sealed class SessionServiceTests : IDisposable
{
    private const string TabA = "tab-a";
    private const string TabB = "tab-b";

    private readonly TempWorkspace _workspace = new();
    private readonly TestDbContextFactory _factory;
    private readonly SessionService _sessions;

    public SessionServiceTests()
    {
        _factory = new TestDbContextFactory($"Data Source={_workspace.Path("sessions.db")}");
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        _sessions = new SessionService(_factory, TestOptions.Create(o => o.Auth.PageCloseGraceSeconds = 5));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task ASecondSignInIsRefusedWhileTheFirstIsActive()
    {
        var user = CreateUser("alice", 1000);
        var first = await _sessions.CreateAsync(user, "10.0.0.1", "browser-a");

        var conflict = await Assert.ThrowsAsync<AlreadySignedInException>(() => _sessions.CreateAsync(user, "10.0.0.2", "browser-b"));

        Assert.Equal("alice", conflict.UserName);

        // The first session survives untouched.
        var validation = await _sessions.ValidateAsync(first.Token, TabA);
        Assert.NotNull(validation.User);
    }

    [Fact]
    public async Task RepeatedSignInsNeverProduceASecondSession()
    {
        var user = CreateUser("alice", 1000);
        await _sessions.CreateAsync(user, null, null);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<AlreadySignedInException>(() => _sessions.CreateAsync(user, null, null));
        }

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Sessions.CountAsync(s => s.UserId == user.Id));
        Assert.Equal(1, await db.Sessions.CountAsync(s => s.UserId == user.Id && s.RevokedUtc == null));
    }

    [Fact]
    public async Task SessionsOfDifferentUsersDoNotInterfere()
    {
        var alice = CreateUser("alice", 1000);
        var bob = CreateUser("bob", 1001);

        var aliceSession = await _sessions.CreateAsync(alice, null, null);
        var bobSession = await _sessions.CreateAsync(bob, null, null);

        Assert.NotNull(await _sessions.ValidateAsync(aliceSession.Token, null));
        Assert.NotNull(await _sessions.ValidateAsync(bobSession.Token, null));
    }

    [Fact]
    public async Task DatabaseRejectsASecondLiveSessionRow()
    {
        var user = CreateUser("alice", 1000);
        await _sessions.CreateAsync(user, null, null);

        await using var db = _factory.CreateDbContext();
        db.Sessions.Add(new SessionRecord
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = "manual",
            CreatedUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(1),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task OnlyTheFirstTabOwnsTheSession()
    {
        var user = CreateUser("alice", 1000);
        var session = await _sessions.CreateAsync(user, null, null);

        var owner = await _sessions.ValidateAsync(session.Token, TabA);
        Assert.NotNull(owner.User);
        Assert.False(owner.TabConflict);

        var second = await _sessions.ValidateAsync(session.Token, TabB);
        Assert.True(second.TabConflict);
        Assert.NotNull(second.User);

        // The owning tab keeps working.
        Assert.False((await _sessions.ValidateAsync(session.Token, TabA)).TabConflict);
    }

    [Fact]
    public async Task ClientsWithoutATabIdentifierAreNotConstrained()
    {
        var user = CreateUser("alice", 1000);
        var session = await _sessions.CreateAsync(user, null, null);

        Assert.NotNull((await _sessions.ValidateAsync(session.Token, null)).User);
        Assert.NotNull((await _sessions.ValidateAsync(session.Token, null)).User);
    }

    [Fact]
    public async Task PageCloseIsCancelledByARequestInsideTheGraceWindow()
    {
        var user = CreateUser("alice", 1000);
        var session = await _sessions.CreateAsync(user, null, null);
        await _sessions.ValidateAsync(session.Token, TabA);

        await _sessions.MarkPageClosedAsync(session.Token, TabA);
        // The reloaded page shows up as the same tab.
        var afterReload = await _sessions.ValidateAsync(session.Token, TabA);

        Assert.NotNull(afterReload.User);
        Assert.False(afterReload.TabConflict);

        await using var db = _factory.CreateDbContext();
        Assert.Null((await db.Sessions.SingleAsync()).ClosedAtUtc);
    }

    [Fact]
    public async Task AReopenedTabInsideTheGraceWindowResumesTheSession()
    {
        var user = CreateUser("alice", 1000);
        var session = await _sessions.CreateAsync(user, null, null);
        await _sessions.ValidateAsync(session.Token, TabA);

        await _sessions.MarkPageClosedAsync(session.Token, TabA);
        var reopened = await _sessions.ValidateAsync(session.Token, TabB);

        Assert.NotNull(reopened.User);
        Assert.False(reopened.TabConflict);

        await using var db = _factory.CreateDbContext();
        var record = await db.Sessions.SingleAsync();
        Assert.Null(record.ClosedAtUtc);
        Assert.Equal(TabB, record.ActiveTabId);
    }

    [Fact]
    public async Task AfterTheGraceWindowTheSessionIsEndedAndFreesTheAccount()
    {
        var user = CreateUser("alice", 1000);
        var session = await _sessions.CreateAsync(user, null, null);
        await _sessions.ValidateAsync(session.Token, TabA);
        await _sessions.MarkPageClosedAsync(session.Token, TabA);

        ExpireGraceWindow();

        var validation = await _sessions.ValidateAsync(session.Token, TabA);
        Assert.Null(validation.User);
        Assert.Equal(SessionRevocationReasons.PageClosed, validation.ReasonCode);

        // The abandoned session no longer blocks a sign-in from another browser.
        var fresh = await _sessions.CreateAsync(user, null, null);
        Assert.NotNull((await _sessions.ValidateAsync(fresh.Token, null)).User);
    }

    [Fact]
    public async Task AClosingTabThatDoesNotOwnTheSessionCannotEndIt()
    {
        var user = CreateUser("alice", 1000);
        var session = await _sessions.CreateAsync(user, null, null);
        await _sessions.ValidateAsync(session.Token, TabA);

        await _sessions.MarkPageClosedAsync(session.Token, TabB);

        await using var db = _factory.CreateDbContext();
        var record = await db.Sessions.SingleAsync();
        Assert.Null(record.ClosedAtUtc);
        Assert.Equal(TabA, record.ActiveTabId);
    }

    [Fact]
    public async Task AdministratorsSeeOnlyLiveSessions()
    {
        var alice = CreateUser("alice", 1000);
        var bob = CreateUser("bob", 1001);
        var session = await _sessions.CreateAsync(alice, "10.0.0.1", "browser");
        await _sessions.ValidateAsync(session.Token, TabA);

        var live = await _sessions.GetActiveAsync();

        var entry = Assert.Single(live);
        Assert.Equal("alice", entry.UserName);
        Assert.Equal("10.0.0.1", entry.RemoteIp);
        Assert.Equal("browser", entry.UserAgent);
        Assert.Equal(TabA, entry.ActiveTabId);
        Assert.False(entry.PendingPageClose);
        Assert.True(entry.LastSeenUtc <= DateTime.UtcNow);

        Assert.NotNull(await _sessions.GetActiveForUserAsync(alice.Id));
        Assert.Null(await _sessions.GetActiveForUserAsync(bob.Id));

        // Closing it (as an administrator would) empties the list.
        await _sessions.RevokeAsync(session.Token, SessionRevocationReasons.ClosedByAdmin);

        Assert.Empty(await _sessions.GetActiveAsync());
        Assert.Null(await _sessions.GetActiveForUserAsync(alice.Id));
    }

    [Fact]
    public async Task SessionsWithAClosedPageAreReportedAndThenDropped()
    {
        var alice = CreateUser("alice", 1000);
        var session = await _sessions.CreateAsync(alice, null, null);
        await _sessions.ValidateAsync(session.Token, TabA);

        await _sessions.MarkPageClosedAsync(session.Token, TabA);

        var pending = Assert.Single(await _sessions.GetActiveAsync());
        Assert.True(pending.PendingPageClose);

        ExpireGraceWindow();

        Assert.Empty(await _sessions.GetActiveAsync());
    }

    [Fact]
    public async Task ExpiredSessionsAreNotListed()
    {
        var alice = CreateUser("alice", 1000);
        await _sessions.CreateAsync(alice, null, null);

        await using (var db = _factory.CreateDbContext())
        {
            var record = await db.Sessions.SingleAsync();
            record.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.Empty(await _sessions.GetActiveAsync());
    }

    [Fact]
    public async Task PasswordRotationKeepsTheCurrentClientSignedIn()
    {
        var user = CreateUser("alice", 1000);
        var desktop = await _sessions.CreateAsync(user, null, "desktop");

        var revoked = await _sessions.RevokeAllAsync(user.Id, SessionRevocationReasons.PasswordChanged, SessionService.Hash(desktop.Token));

        Assert.Equal(0, revoked);
        Assert.NotNull(await _sessions.ValidateAsync(desktop.Token, null));
    }

    [Fact]
    public async Task PasswordRotationEndsOtherSessions()
    {
        var user = CreateUser("alice", 1000);
        var first = await _sessions.CreateAsync(user, null, "phone");
        await _sessions.RevokeAsync(first.Token, SessionRevocationReasons.PageClosed);

        var second = await _sessions.CreateAsync(user, null, "desktop");
        var revoked = await _sessions.RevokeAllAsync(user.Id, SessionRevocationReasons.PasswordChanged, exceptTokenHash: null);

        Assert.Equal(1, revoked);
        Assert.Null((await _sessions.ValidateAsync(second.Token, null)).User);
    }

    [Fact]
    public async Task LogoutMarksTheSessionSignedOutAndReleasesTheAccount()
    {
        var user = CreateUser("alice", 1000);
        var session = await _sessions.CreateAsync(user, null, null);
        await _sessions.ValidateAsync(session.Token, TabA);

        await _sessions.RevokeAsync(session.Token, SessionRevocationReasons.SignedOut);

        var validation = await _sessions.ValidateAsync(session.Token, TabA);
        Assert.Null(validation.User);
        Assert.Equal(SessionRevocationReasons.SignedOut, validation.ReasonCode);

        // After an explicit sign-out the account is available again.
        var fresh = await _sessions.CreateAsync(user, null, null);
        Assert.NotNull((await _sessions.ValidateAsync(fresh.Token, null)).User);
    }

    [Fact]
    public async Task ExpiredSessionsReportExpiryAndDoNotBlockANewSignIn()
    {
        var user = CreateUser("alice", 1000);
        var session = await _sessions.CreateAsync(user, null, null);

        await using (var db = _factory.CreateDbContext())
        {
            var record = await db.Sessions.SingleAsync();
            record.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var validation = await _sessions.ValidateAsync(session.Token, null);
        Assert.Null(validation.User);
        Assert.Equal(SessionRevocationReasons.Expired, validation.ReasonCode);

        var fresh = await _sessions.CreateAsync(user, null, null);
        Assert.Equal(1, fresh.RevokedSessions);
        Assert.NotNull(await _sessions.ValidateAsync(fresh.Token, null));
    }

    [Fact]
    public async Task UnknownTokenIsSimplyNotAuthenticated()
    {
        var validation = await _sessions.ValidateAsync("not-a-session", null);

        Assert.Null(validation.User);
        Assert.Null(validation.ReasonCode);
    }

    /// <summary>Moves the recorded close into the past so the grace window is over.</summary>
    private void ExpireGraceWindow()
    {
        using var db = _factory.CreateDbContext();
        var record = db.Sessions.Single();
        record.ClosedAtUtc = DateTime.UtcNow.AddSeconds(-30);
        db.SaveChanges();
    }

    private AppUser CreateUser(string name, uint uid)
    {
        using var db = _factory.CreateDbContext();
        var user = new AppUser
        {
            UserName = name,
            Uid = uid,
            IsAdmin = false,
            FirstSeenUtc = DateTime.UtcNow,
            LastLoginUtc = DateTime.UtcNow,
        };

        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<FileManagerDbContext>
    {
        private readonly DbContextOptions<FileManagerDbContext> _options;

        public TestDbContextFactory(string connectionString)
        {
            _options = new DbContextOptionsBuilder<FileManagerDbContext>()
                .UseSqlite(connectionString)
                .Options;
        }

        public FileManagerDbContext CreateDbContext() => new(_options);
    }
}
