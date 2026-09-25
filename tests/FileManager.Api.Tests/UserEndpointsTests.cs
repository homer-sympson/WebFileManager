using System.Net;
using System.Text.Json;

namespace FileManager.Api.Tests;

/// <summary>
/// Only non-mutating user endpoints and pre-flight validation are exercised here: creating a real
/// account would touch the host /etc. The full create/update/delete flows are covered by
/// FileManager.Core.Tests with a command runner double.
/// </summary>
public class UserEndpointsTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Non_admins_cannot_manage_users()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("alice");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users/groups")).StatusCode);
    }

    [Fact]
    public async Task Admins_list_only_login_capable_accounts_by_default()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("admin");

        var response = await client.GetAsync("/api/users/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.ReadJsonAsync();
        var names = payload.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("userName").GetString()).ToArray();

        Assert.Contains("alice", names);
        Assert.Contains("admin", names);
        Assert.DoesNotContain("nopassword", names);
        Assert.DoesNotContain("service", names);

        var withSystem = await (await client.GetAsync("/api/users/?includeSystem=true")).ReadJsonAsync();
        var allNames = withSystem.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("userName").GetString()).ToArray();
        Assert.Contains("nopassword", allNames);

        var admin = payload.GetProperty("users").EnumerateArray().First(u => u.GetProperty("userName").GetString() == "admin");
        Assert.True(admin.GetProperty("isAdmin").GetBoolean());
        Assert.True(admin.GetProperty("hasPassword").GetBoolean());
        Assert.Contains("sudo", admin.GetProperty("groups").EnumerateArray().Select(g => g.GetString()));
    }

    [Fact]
    public async Task Groups_endpoint_lists_host_groups()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("admin");

        var groups = await (await client.GetAsync("/api/users/groups")).ReadJsonAsync();

        var names = groups.EnumerateArray().Select(g => g.GetString()).ToArray();
        Assert.Contains("sudo", names);
        Assert.Contains("wheel", names);
    }

    [Fact]
    public async Task User_details_report_acl_state_without_failing()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("admin");

        var url = $"/api/users/alice?paths={Uri.EscapeDataString(_factory.Share)}";
        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var details = await response.ReadJsonAsync();
        Assert.Equal("alice", details.GetProperty("name").GetString());
        Assert.False(details.GetProperty("hasSudoRule").GetBoolean());

        var access = details.GetProperty("access").EnumerateArray().Single();
        Assert.Equal(_factory.Share, access.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Unknown_user_returns_not_found()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("admin");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/users/ghost")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteApiAsync("/api/users/ghost")).StatusCode);
    }

    [Fact]
    public async Task Deleting_root_is_forbidden()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("admin");

        var response = await client.DeleteApiAsync("/api/users/root?removeHome=false");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admins_cannot_delete_their_own_account()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("admin");

        var response = await client.DeleteApiAsync("/api/users/admin");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Alice", "Sup3rSecret!")]
    [InlineData("root", "Sup3rSecret!")]
    [InlineData("newbie", "short")]
    [InlineData("newbie", "")]
    public async Task Creating_a_user_validates_input_before_touching_the_host(string userName, string password)
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("admin");

        var response = await client.PostJsonAsync("/api/users/", new
        {
            userName,
            password,
            fullName = (string?)null,
            shell = "/bin/bash",
            createHome = true,
            homeDirectory = (string?)null,
            groups = Array.Empty<string>(),
            grantSudo = false,
            sudoNopasswd = false,
            pathGrants = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Creating_an_existing_user_conflicts_before_touching_the_host()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("admin");

        var response = await client.PostJsonAsync("/api/users/", new
        {
            userName = "alice",
            password = "Sup3rSecret!",
            fullName = (string?)null,
            shell = "/bin/bash",
            createHome = true,
            homeDirectory = (string?)null,
            groups = Array.Empty<string>(),
            grantSudo = false,
            sudoNopasswd = false,
            pathGrants = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("Sup3rSecret!", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passwords_are_never_echoed_back()
    {
        using var client = _factory.CreateApiClient();
        await client.LoginAsync("admin");

        var details = await (await client.GetAsync("/api/users/alice")).ReadJsonAsync();
        var body = details.GetRawText();

        // The API reports only the boolean flag, never a hash or the submitted secret.
        Assert.DoesNotContain("$6$", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret123", body, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordDemand", body, StringComparison.Ordinal);
        Assert.Contains("hasUsablePassword", body, StringComparison.Ordinal);
    }
}
