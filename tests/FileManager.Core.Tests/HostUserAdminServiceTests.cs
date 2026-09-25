using FileManager.Core;
using FileManager.Core.Configuration;
using FileManager.Core.Linux;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests;

/// <summary>
/// Emulates the host account database: useradd/chpasswd/usermod/groupadd/userdel/groupdel edit the
/// fixture /etc/passwd, /etc/shadow and /etc/group files, which the real reader then parses.
/// </summary>
public sealed class FakeHost : IDisposable
{
    private const string InitialPasswd = """
        root:x:0:0:root:/root:/bin/bash
        alice:x:1000:1000:Alice:/home/alice:/bin/bash
        """;

    private const string InitialGroup = """
        root:x:0:
        sudo:x:27:alice
        wheel:x:10:
        alice:x:1000:
        """;

    private const string InitialShadow = """
        root:*:19000:0:99999:7:::
        alice:$6$abcdefgh$L2RkWuRRaXbSPKE2h075RMaIsfsrCKiKdR4CAmWFZdxb7FD5ntuy3JlinIGZEpWPL2s7jolhp7uEtLBxC/4Xq1:19000:0:99999:7:::
        """;

    private readonly TempWorkspace _workspace;
    private int _nextUid = 2000;

    public FakeHost()
    {
        _workspace = new TempWorkspace();
        PasswdPath = _workspace.Write("etc/passwd", EnsureTrailingNewLine(InitialPasswd));
        GroupPath = _workspace.Write("etc/group", EnsureTrailingNewLine(InitialGroup));
        ShadowPath = _workspace.Write("etc/shadow", EnsureTrailingNewLine(InitialShadow));
        SudoersDir = _workspace.Directory("etc/sudoers.d");
        Share = _workspace.Directory("srv/share");

        Options = new FileManagerOptions
        {
            BrowseRoots = [_workspace.Root],
            AdminGroups = ["sudo", "wheel"],
            AllowNonRootDev = true,
            Linux = new LinuxPathsOptions
            {
                Passwd = PasswdPath,
                Shadow = ShadowPath,
                Group = GroupPath,
                SudoersDir = SudoersDir,
                Visudo = "visudo",
            },
            Auth = new AuthOptions { MinPasswordLength = 8 },
        };

        Runner = new FakeProcessRunner { Handler = Handle };
        Runner.Existing.Add("setfacl");
        Runner.Existing.Add("getfacl");

        Directory = TestOptions.CreateDirectory(Options);
        Acl = new StubAclService();
        Sudoers = new SudoersService(Runner, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<SudoersService>.Instance);
        Service = new HostUserAdminService(
            Runner,
            Directory,
            Sudoers,
            Acl,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<HostUserAdminService>.Instance);
    }

    public string PasswdPath { get; }

    public string GroupPath { get; }

    public string ShadowPath { get; }

    public string SudoersDir { get; }

    public string Share { get; }

    public FileManagerOptions Options { get; }

    public FakeProcessRunner Runner { get; }

    public LinuxHostUserDirectory Directory { get; }

    public StubAclService Acl { get; }

    public SudoersService Sudoers { get; }

    public HostUserAdminService Service { get; }

    public string? PasswordFor(string userName)
    {
        foreach (var line in File.ReadAllLines(ShadowPath))
        {
            var parts = line.Split(':');
            if (parts.Length > 1 && parts[0] == userName)
            {
                return parts[1];
            }
        }

        return null;
    }

    public bool UserExists(string userName) => File.ReadAllLines(PasswdPath).Any(l => l.Split(':')[0] == userName);

    public void Dispose() => _workspace.Dispose();

    private static string EnsureTrailingNewLine(string content) =>
        content.EndsWith('\n') ? content : content + Environment.NewLine;

    private ProcessResult Handle(string file, IReadOnlyList<string> arguments, string? standardInput)
    {
        switch (file)
        {
            case "visudo":
                return new ProcessResult(0, string.Empty, string.Empty);

            case "useradd":
            {
                var name = arguments[^1];
                var uid = _nextUid++;
                var shell = arguments.Contains("-s") ? arguments[arguments.ToList().IndexOf("-s") + 1] : "/bin/sh";
                var home = arguments.Contains("-d") ? arguments[arguments.ToList().IndexOf("-d") + 1] : $"/home/{name}";
                var comment = arguments.Contains("-c") ? arguments[arguments.ToList().IndexOf("-c") + 1] : name;
                File.AppendAllText(PasswdPath, $"{name}:x:{uid}:{uid}:{comment}:{home}:{shell}{Environment.NewLine}");
                File.AppendAllText(GroupPath, $"{name}:x:{uid}:{Environment.NewLine}");
                File.AppendAllText(ShadowPath, $"{name}!:{19000 + uid}:0:99999:7:::{Environment.NewLine}");
                return new ProcessResult(0, string.Empty, string.Empty);
            }

            case "chpasswd":
            {
                var payload = (standardInput ?? string.Empty).Trim();
                var separator = payload.IndexOf(':');
                if (separator <= 0)
                {
                    return new ProcessResult(1, string.Empty, "malformed input");
                }

                var name = payload[..separator];
                var password = payload[(separator + 1)..];
                var lines = File.ReadAllLines(ShadowPath).ToList();
                var index = lines.FindIndex(l => l.StartsWith($"{name}:", StringComparison.Ordinal));
                var hash = password == "Sup3rSecret!" ? "$6$fixture$abcdefghijklmnopqrstuvwxyz0123456789" : "$6$other$hash";
                var newLine = $"{name}:{hash}:19000:0:99999:7:::";
                if (index >= 0)
                {
                    lines[index] = newLine;
                }
                else
                {
                    lines.Add(newLine);
                }

                File.WriteAllLines(ShadowPath, lines);
                return new ProcessResult(0, string.Empty, string.Empty);
            }

            case "usermod":
            {
                var list = arguments.ToList();
                var groupIndex = list.IndexOf("-aG") >= 0 ? list.IndexOf("-aG") : list.IndexOf("-G");
                if (groupIndex < 0)
                {
                    return new ProcessResult(0, string.Empty, string.Empty);
                }

                var groups = arguments[groupIndex + 1];
                var name = arguments[^1];
                var lines = File.ReadAllLines(GroupPath).ToList();
                foreach (var group in groups.Split(','))
                {
                    var index = lines.FindIndex(l => l.StartsWith($"{group}:", StringComparison.Ordinal));
                    if (index < 0)
                    {
                        continue;
                    }

                    var parts = lines[index].Split(':');
                    var members = parts[3].Length == 0 ? new List<string>() : parts[3].Split(',').ToList();
                    if (!members.Contains(name))
                    {
                        members.Add(name);
                    }

                    lines[index] = $"{parts[0]}:{parts[1]}:{parts[2]}:{string.Join(',', members)}";
                }

                File.WriteAllLines(GroupPath, lines);
                return new ProcessResult(0, string.Empty, string.Empty);
            }

            case "groupadd":
            {
                var name = arguments[^1];
                File.AppendAllText(GroupPath, $"{name}:x:{3000 + File.ReadAllLines(GroupPath).Length}:{Environment.NewLine}");
                return new ProcessResult(0, string.Empty, string.Empty);
            }

            case "userdel":
            {
                var name = arguments[^1];
                File.WriteAllLines(PasswdPath, File.ReadAllLines(PasswdPath).Where(l => !l.StartsWith($"{name}:", StringComparison.Ordinal)));
                File.WriteAllLines(ShadowPath, File.ReadAllLines(ShadowPath).Where(l => !l.StartsWith($"{name}:", StringComparison.Ordinal)));
                File.WriteAllLines(GroupPath, File.ReadAllLines(GroupPath).Where(l => !l.StartsWith($"{name}:", StringComparison.Ordinal)));
                return new ProcessResult(0, string.Empty, string.Empty);
            }

            default:
                return new ProcessResult(0, string.Empty, string.Empty);
        }
    }
}

public class HostUserAdminServiceTests
{
    [Fact]
    public async Task CreatesUserWithPasswordGroupsSudoAndAcl()
    {
        using var host = new FakeHost();

        var details = await host.Service.CreateAsync(new CreateHostUserRequest(
            "deploy",
            "Sup3rSecret!",
            "Deploy Bot",
            "/bin/bash",
            CreateHome: true,
            HomeDirectory: null,
            Groups: ["docker"],
            GrantSudo: true,
            SudoNopasswd: false,
            PathGrants: [new PathGrant(host.Share, PathAccess.ReadWrite, true)]));

        Assert.Equal("deploy", details.Name);
        Assert.True(details.HasSudoRule);
        Assert.True(host.UserExists("deploy"));

        var useradd = host.Runner.Single("useradd");
        Assert.NotNull(useradd);
        Assert.Equal("deploy", useradd!.Arguments[^1]);
        Assert.True(useradd.HasArgument("-m"));
        Assert.Equal("Deploy Bot", useradd.ArgumentAfter("-c"));

        var chpasswd = host.Runner.Single("chpasswd");
        Assert.NotNull(chpasswd);
        Assert.Equal($"deploy:Sup3rSecret!{Environment.NewLine}", chpasswd!.StandardInput);
        Assert.Empty(chpasswd.Arguments);

        Assert.NotNull(host.Runner.Single("groupadd"));
        var usermod = host.Runner.Single("usermod");
        Assert.NotNull(usermod);
        // grantSudo also adds the admin group so the application recognises the account as an admin.
        Assert.Equal("docker,sudo", usermod!.ArgumentAfter("-aG"));

        Assert.True(File.Exists(Path.Combine(host.SudoersDir, "deploy")));
        Assert.Contains(host.Acl.Applied, a => a.User == "deploy" && a.Path == host.Share && a.Access == PathAccess.ReadWrite && a.Default);
        Assert.True(details.HasUsablePassword);
        Assert.Contains("docker", details.Groups);
    }

    [Theory]
    [InlineData("Alice", "Sup3rSecret!")]
    [InlineData("root", "Sup3rSecret!")]
    [InlineData("deploy", "short1")]
    [InlineData("deploy", "with:colon!")]
    [InlineData("deploy", "")]
    public async Task RejectsInvalidRegistrations(string userName, string password)
    {
        using var host = new FakeHost();

        var exception = await Assert.ThrowsAsync<FileManagerException>(() => host.Service.CreateAsync(new CreateHostUserRequest(
            userName,
            password,
            null,
            "/bin/bash",
            true,
            null,
            [],
            false,
            false,
            [])));

        Assert.Equal(HttpStatus.BadRequest, exception.StatusCode);
        Assert.Empty(host.Runner.All("useradd"));
    }

    [Fact]
    public async Task RejectsDuplicateUser()
    {
        using var host = new FakeHost();

        var exception = await Assert.ThrowsAsync<FileManagerException>(() => host.Service.CreateAsync(new CreateHostUserRequest(
            "alice",
            "Sup3rSecret!",
            null,
            "/bin/bash",
            true,
            null,
            [],
            false,
            false,
            [])));

        Assert.Equal(HttpStatus.Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task RollsBackEverythingWhenUsermodFails()
    {
        using var host = new FakeHost();
        var original = host.Runner.Handler!;
        host.Runner.Handler = (file, args, input) =>
            file == "usermod" ? new ProcessResult(1, string.Empty, "usermod failed") : original(file, args, input);

        await Assert.ThrowsAsync<FileManagerException>(() => host.Service.CreateAsync(new CreateHostUserRequest(
            "deploy",
            "Sup3rSecret!",
            null,
            "/bin/bash",
            true,
            null,
            ["docker"],
            true,
            false,
            [new PathGrant(host.Share, PathAccess.Read, true)])));

        Assert.False(host.UserExists("deploy"));
        Assert.NotNull(host.Runner.Single("userdel"));
        Assert.False(File.Exists(Path.Combine(host.SudoersDir, "deploy")));
    }

    [Fact]
    public async Task UpdateSetsPasswordAndGrants()
    {
        using var host = new FakeHost();

        var details = await host.Service.UpdateAsync("alice", new UpdateHostUserRequest(
            FullName: "Alice Updated",
            Shell: "/bin/zsh",
            Password: "Sup3rSecret!",
            Groups: ["docker"],
            GrantSudo: true,
            SudoNopasswd: true,
            PathGrants: [new PathGrant(host.Share, PathAccess.Read, true)],
            RevokeAclPaths: null));

        Assert.Contains("NOPASSWD", details.SudoRule ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(host.Acl.Applied, a => a.User == "alice" && a.Path == host.Share);
        Assert.Contains("$6$fixture$", host.PasswordFor("alice") ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(host.Runner.All("usermod"), c => c.HasArgument("-G"));
    }

    [Fact]
    public async Task DeleteRemovesSudoRuleAndAccount()
    {
        using var host = new FakeHost();
        await host.Sudoers.ApplyAsync("alice", nopasswd: false);
        Assert.True(File.Exists(Path.Combine(host.SudoersDir, "alice")));

        await host.Service.DeleteAsync("alice", removeHome: true, [host.Share]);

        Assert.False(host.UserExists("alice"));
        Assert.False(File.Exists(Path.Combine(host.SudoersDir, "alice")));
        Assert.Contains(("alice", host.Share), host.Acl.Removed);
        Assert.Contains("-r", host.Runner.Single("userdel")!.Arguments);
    }

    [Fact]
    public async Task DeleteRefusesRoot()
    {
        using var host = new FakeHost();

        var exception = await Assert.ThrowsAsync<FileManagerException>(() => host.Service.DeleteAsync("root", false, []));

        Assert.Equal(HttpStatus.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task DetailsReportAclEntriesFromTheOperatingSystem()
    {
        using var host = new FakeHost();
        await host.Service.CreateAsync(new CreateHostUserRequest(
            "deploy",
            "Sup3rSecret!",
            null,
            "/bin/bash",
            true,
            null,
            [],
            false,
            false,
            [new PathGrant(host.Share, PathAccess.ReadWrite, true)]));

        var details = await host.Service.GetAsync("deploy", [host.Share]);

        Assert.Single(details.Access);
        Assert.Null(details.Access[0].Error);
        Assert.Contains(details.Access[0].Entries, e => e.Permissions == "rwX");
    }

    [Fact]
    public async Task ReportsMissingAclToolingInsteadOfFailing()
    {
        using var host = new FakeHost();
        host.Acl.IsAvailable = false;

        var details = await host.Service.GetAsync("alice", [host.Share]);

        Assert.Single(details.Access);
        Assert.NotNull(details.Access[0].Error);
    }
}
