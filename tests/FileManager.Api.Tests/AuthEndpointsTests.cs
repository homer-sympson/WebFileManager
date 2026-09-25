using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FileManager.Api.Tests;

public class AuthEndpointsTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Anonymous_file_requests_are_rejected()
    {
        using var client = _factory.CreateApiClient();

        var response = await client.GetAsync($"/api/fs/list?path={Uri.EscapeDataString(_factory.Share)}");
        var me = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Login_creates_a_session_cookie_and_me_returns_the_profile()
    {
        using var client = _factory.CreateApiClient();

        var sessionCookie = await LoginAndGetCookieAsync(client, "alice");
        Assert.Contains("fm_session", sessionCookie, StringComparison.Ordinal);

        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var profile = await me.ReadJsonAsync();
        Assert.Equal("alice", profile.GetProperty("userName").GetString());
        Assert.False(profile.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(1000u, profile.GetProperty("uid").GetUInt32());
    }

    [Fact]
    public async Task Admin_profile_is_flagged_as_admin()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("admin");

        var profile = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();

        Assert.True(profile.GetProperty("isAdmin").GetBoolean());
    }

    [Theory]
    [InlineData("nosuchuser")]
    [InlineData("nopassword")]
    [InlineData("service")]
    [InlineData("")]
    public async Task Login_rejects_unknown_passwordless_and_nologin_accounts(string userName)
    {
        using var client = _factory.CreateApiClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { userName, password = "secret123" }),
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("fm_session", response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join(';', values) : string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_options_only_list_login_capable_accounts()
    {
        using var client = _factory.CreateApiClient();

        var options = await (await client.GetAsync("/api/auth/login-options")).ReadJsonAsync();

        Assert.True(options.GetProperty("exposeUserList").GetBoolean());
        var users = options.GetProperty("users").EnumerateArray().Select(u => u.GetString()).ToArray();
        Assert.Contains("alice", users);
        Assert.Contains("admin", users);
        Assert.DoesNotContain("nopassword", users);
        Assert.DoesNotContain("service", users);
        Assert.DoesNotContain("root", users);
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");

        var logout = await client.PostJsonAsync("/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_second_browser_cannot_sign_in_while_the_account_is_in_use()
    {
        using var first = _factory.CreateApiClient();
        using var second = _factory.CreateApiClient();

        await first.LoginAsync("alice");
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/auth/me")).StatusCode);

        var refused = await second.PostJsonAsync("/api/auth/login", new { userName = "alice", password = "secret123" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var problem = await refused.ReadJsonAsync();
        Assert.Equal("already-signed-in", problem.GetProperty("code").GetString());
        Assert.Contains("другом браузере", problem.GetProperty("detail").GetString() ?? string.Empty, StringComparison.Ordinal);
        Assert.True(problem.TryGetProperty("lastSeenUtc", out _), "the refusal reports when the session was last active");

        // The first browser is untouched and no second session was created.
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/auth/me")).StatusCode);

        await using var db = _factory.CreateDbContext();
        var alice = db.Users.Single(u => u.UserName == "alice");
        Assert.Equal(1, db.Sessions.Count(s => s.UserId == alice.Id));
        Assert.Equal(1, db.Sessions.Count(s => s.UserId == alice.Id && s.RevokedUtc == null));
        Assert.Contains(db.AuditLog, e => e.Action == "login.refused" && e.ActorUserName == "alice");
    }

    [Fact]
    public async Task Signing_in_works_again_after_the_first_browser_signs_out()
    {
        using var first = _factory.CreateApiClient();
        using var second = _factory.CreateApiClient();

        await first.LoginAsync("alice");
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostJsonAsync("/api/auth/login", new { userName = "alice", password = "secret123" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await first.PostJsonAsync("/api/auth/logout", new { })).StatusCode);

        var login = await second.PostJsonAsync("/api/auth/login", new { userName = "alice", password = "secret123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Closing_the_page_frees_the_account_for_another_browser()
    {
        using var first = _factory.CreateApiClient();
        using var second = _factory.CreateApiClient();
        var token = await first.LoginForTokenAsync("alice");

        // sendBeacon cannot set X-Requested-With; the endpoint must stay reachable without it.
        using var beacon = _factory.CreateSessionClient(token, "tab-1");
        var closed = await beacon.PostAsync("/api/auth/page-closed?tabId=tab-1", content: null);
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);

        // Inside the grace window the same browser may still come back...
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/auth/me")).StatusCode);

        // ...so close it again and wait for the window to pass.
        await beacon.PostAsync("/api/auth/page-closed?tabId=tab-1", content: null);
        await Task.Delay(TimeSpan.FromMilliseconds(1300));

        var login = await second.PostJsonAsync("/api/auth/login", new { userName = "alice", password = "secret123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Only_one_tab_of_a_browser_can_use_the_session()
    {
        using var loginClient = _factory.CreateApiClient(handleCookies: false);
        var token = await loginClient.LoginForTokenAsync("alice");

        using var tabA = _factory.CreateSessionClient(token, "tab-a");
        using var tabB = _factory.CreateSessionClient(token, "tab-b");

        Assert.Equal(HttpStatusCode.OK, (await tabA.GetAsync("/api/auth/me")).StatusCode);

        var conflict = await tabB.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("tab-conflict", (await conflict.ReadJsonAsync()).GetProperty("code").GetString());

        // The tab that claimed the session keeps working.
        Assert.Equal(HttpStatusCode.OK, (await tabA.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task An_administrator_can_close_a_session_that_the_owner_cannot_end()
    {
        using var aliceBrowser = _factory.CreateApiClient();
        using var adminBrowser = _factory.CreateApiClient();

        // Alice's browser "crashed": her session stays live and blocks every other sign-in.
        await aliceBrowser.LoginAsync("alice");
        await adminBrowser.LoginAsync("admin");

        using var aliceAgain = _factory.CreateApiClient();
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await aliceAgain.PostJsonAsync("/api/auth/login", new { userName = "alice", password = "secret123" })).StatusCode);

        var listed = await (await adminBrowser.GetAsync("/api/sessions/")).ReadJsonAsync();
        var aliceSession = listed.GetProperty("sessions").EnumerateArray()
            .Single(s => s.GetProperty("userName").GetString() == "alice");
        Assert.False(aliceSession.GetProperty("isCurrentSession").GetBoolean());
        Assert.True(aliceSession.GetProperty("createdUtc").GetString()!.Length > 0);

        var closed = await adminBrowser.DeleteApiAsync("/api/sessions/alice");
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);

        // Alice's stale cookie is dead...
        var stale = await aliceBrowser.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(
            "session-closed-by-admin",
            (await stale.ReadJsonAsync()).GetProperty("code").GetString());

        // ...and the account is free again.
        Assert.Equal(
            HttpStatusCode.OK,
            (await aliceAgain.PostJsonAsync("/api/auth/login", new { userName = "alice", password = "secret123" })).StatusCode);

        await using var db = _factory.CreateDbContext();
        Assert.Contains(db.AuditLog, e => e.Action == "session.admin-closed" && e.ActorUserName == "admin");
    }

    [Fact]
    public async Task Only_administrators_see_or_close_sessions()
    {
        using var aliceBrowser = _factory.CreateApiClient();
        await aliceBrowser.LoginAsync("alice");

        Assert.Equal(HttpStatusCode.Forbidden, (await aliceBrowser.GetAsync("/api/sessions/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await aliceBrowser.DeleteApiAsync("/api/sessions/alice")).StatusCode);
    }

    [Fact]
    public async Task Closing_an_idle_account_reports_not_found()
    {
        using var adminBrowser = _factory.CreateApiClient();
        await adminBrowser.LoginAsync("admin");

        // Never signed in.
        Assert.Equal(HttpStatusCode.NotFound, (await adminBrowser.DeleteApiAsync("/api/sessions/alice")).StatusCode);
        // Not even a host account.
        Assert.Equal(HttpStatusCode.NotFound, (await adminBrowser.DeleteApiAsync("/api/sessions/ghost")).StatusCode);
    }

    [Fact]
    public async Task An_administrator_can_close_their_own_session()
    {
        using var adminBrowser = _factory.CreateApiClient();
        await adminBrowser.LoginAsync("admin");

        var listed = await (await adminBrowser.GetAsync("/api/sessions/")).ReadJsonAsync();
        var own = listed.GetProperty("sessions").EnumerateArray().Single();
        Assert.True(own.GetProperty("isCurrentSession").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await adminBrowser.DeleteApiAsync("/api/sessions/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await adminBrowser.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Different_users_can_be_signed_in_at_the_same_time()
    {
        using var alice = _factory.CreateApiClient();
        using var admin = _factory.CreateApiClient();

        await alice.LoginAsync("alice");
        await admin.LoginAsync("admin");

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);

        await using var db = _factory.CreateDbContext();
        var live = db.Sessions.Count(s => s.RevokedUtc == null);
        Assert.Equal(2, live);
    }

    [Fact]
    public async Task Repaired_account_loses_its_session_when_the_password_disappears()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");

        // Simulate an administrator clearing the password of the account.
        var lines = File.ReadAllLines(_factory.ShadowPath);
        File.WriteAllLines(_factory.ShadowPath, lines.Select(l => l.StartsWith("alice:", StringComparison.Ordinal) ? ReplaceHash(l, "!") : l));

        try
        {
            var me = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        }
        finally
        {
            File.WriteAllLines(_factory.ShadowPath, lines);
        }
    }

    [Fact]
    public async Task Health_and_capabilities_are_public()
    {
        using var client = _factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var capabilities = await (await client.GetAsync("/api/system/capabilities")).ReadJsonAsync();
        Assert.False(capabilities.GetProperty("impersonationEnabled").GetBoolean());
        Assert.True(capabilities.GetProperty("databaseReady").GetBoolean());
        Assert.True(capabilities.GetProperty("shadowAvailable").GetBoolean());
        var roots = capabilities.GetProperty("browseRoots").EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.Single(roots);
        Assert.Equal(_factory.Share, roots[0]);
    }

    private static string ReplaceHash(string line, string hash)
    {
        var parts = line.Split(':');
        parts[1] = hash;
        return string.Join(':', parts);
    }

    private static async Task<string> LoginAndGetCookieAsync(HttpClient client, string userName)
    {
        var response = await client.PostJsonAsync("/api/auth/login", new { userName, password = "secret123" });
        response.EnsureSuccessStatusCode();
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join(';', values) : string.Empty;
        Assert.True(client is not null);
        return cookies;
    }
}
