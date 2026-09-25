using FileManager.Core;
using FileManager.Core.Configuration;
using FileManager.Core.Linux;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests;

public class AdminBootstrapServiceTests
{
    [Fact]
    public async Task DoesNothingWhenAUsableAdminExists()
    {
        using var host = new FakeHost();
        var bootstrap = Create(host, enabled: true, password: "passwordDemand");

        var result = await bootstrap.EnsureAsync();

        Assert.False(result.Performed);
        Assert.Empty(host.Runner.All("useradd"));
    }

    [Fact]
    public async Task CreatesEmergencyAdministratorWhenNoUsableRootAccountExists()
    {
        using var host = new FakeHost();
        // Remove alice from sudo: the only remaining privileged account is root, which has no password.
        File.WriteAllLines(host.GroupPath, ["root:x:0:", "sudo:x:27:", "wheel:x:10:", "alice:x:1000:"]);

        var bootstrap = Create(host, enabled: true, password: "passwordDemand");
        var result = await bootstrap.EnsureAsync();

        Assert.True(result.Performed);
        Assert.Contains("a-admin", result.Message, StringComparison.Ordinal);

        var useradd = host.Runner.Single("useradd");
        Assert.NotNull(useradd);
        Assert.Equal("a-admin", useradd!.Arguments[^1]);
        Assert.Equal($"a-admin:passwordDemand{Environment.NewLine}", host.Runner.Single("chpasswd")!.StandardInput);
        Assert.True(File.Exists(Path.Combine(host.SudoersDir, "a-admin")));

        var created = host.Directory.Find("a-admin");
        Assert.NotNull(created);
        Assert.True(created!.HasUsablePassword);
        Assert.True(created.IsAdmin);
    }

    [Fact]
    public async Task RepairsExistingAccountWithoutUsablePassword()
    {
        using var host = new FakeHost();
        File.WriteAllLines(host.GroupPath, ["root:x:0:", "sudo:x:27:a-admin", "wheel:x:10:", "alice:x:1000:", "a-admin:x:2000:"]);
        File.AppendAllText(host.PasswdPath, $"a-admin:x:2000:2000::/home/a-admin:/bin/bash{Environment.NewLine}");
        File.AppendAllText(host.ShadowPath, $"a-admin!:{19000}:0:99999:7:::{Environment.NewLine}");

        var bootstrap = Create(host, enabled: true, password: "passwordDemand");
        var result = await bootstrap.EnsureAsync();

        Assert.True(result.Performed);
        Assert.Empty(host.Runner.All("useradd"));
        Assert.NotNull(host.Runner.Single("chpasswd"));
    }

    [Fact]
    public async Task RefusesToStartWithoutAConfiguredPassword()
    {
        using var host = new FakeHost();
        File.WriteAllLines(host.GroupPath, ["root:x:0:", "sudo:x:27:", "wheel:x:10:", "alice:x:1000:"]);

        var bootstrap = Create(host, enabled: true, password: string.Empty);

        var exception = await Assert.ThrowsAsync<FileManagerException>(() => bootstrap.EnsureAsync());
        Assert.Equal(HttpStatus.InternalServerError, exception.StatusCode);
    }

    [Fact]
    public async Task SkipsWhenDisabled()
    {
        using var host = new FakeHost();
        File.WriteAllLines(host.GroupPath, ["root:x:0:", "sudo:x:27:", "wheel:x:10:", "alice:x:1000:"]);

        var bootstrap = Create(host, enabled: false, password: "passwordDemand");
        var result = await bootstrap.EnsureAsync();

        Assert.False(result.Performed);
        Assert.Empty(host.Runner.All("useradd"));
    }

    private static AdminBootstrapService Create(FakeHost host, bool enabled, string password)
    {
        host.Options.Bootstrap = new BootstrapOptions
        {
            Enabled = enabled,
            AdminUser = "a-admin",
            AdminPassword = password,
            GrantSudo = true,
        };

        return new AdminBootstrapService(
            host.Directory,
            new HostUserAdminService(
                host.Runner,
                host.Directory,
                host.Sudoers,
                host.Acl,
                Microsoft.Extensions.Options.Options.Create(host.Options),
                NullLogger<HostUserAdminService>.Instance),
            Microsoft.Extensions.Options.Options.Create(host.Options),
            NullLogger<AdminBootstrapService>.Instance);
    }
}
