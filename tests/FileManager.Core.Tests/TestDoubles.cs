using FileManager.Core.Configuration;
using FileManager.Core.Linux;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Tests;

/// <summary>Records external commands instead of executing them.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public Func<string, IReadOnlyList<string>, string?, ProcessResult>? Handler { get; set; }

    public bool ExistsAlways { get; set; } = true;

    public HashSet<string> Existing { get; } = new(StringComparer.Ordinal);

    public List<Invocation> Calls { get; } = [];

    public bool Exists(string executable) => ExistsAlways || Existing.Contains(executable);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(new Invocation(fileName, arguments.ToArray(), standardInput));
        var result = Handler?.Invoke(fileName, arguments, standardInput) ?? new ProcessResult(0, string.Empty, string.Empty);
        return Task.FromResult(result);
    }

    public Invocation? Single(string fileName) => Calls.SingleOrDefault(c => c.FileName == fileName);

    public IEnumerable<Invocation> All(string fileName) => Calls.Where(c => c.FileName == fileName);

    public sealed record Invocation(string FileName, IReadOnlyList<string> Arguments, string? StandardInput)
    {
        public bool HasArgument(string value) => Arguments.Contains(value, StringComparer.Ordinal);

        public string ArgumentAfter(string flag)
        {
            var index = Arguments.ToList().IndexOf(flag);
            return index >= 0 && index + 1 < Arguments.Count ? Arguments[index + 1] : string.Empty;
        }
    }
}

/// <summary>In-memory POSIX ACL double: the real setfacl binary is not installed in every environment.</summary>
public sealed class StubAclService : IPosixAclService
{
    public bool IsAvailable { get; set; } = true;

    public List<(string User, string Path, PathAccess Access, bool Default)> Applied { get; } = [];

    public List<(string User, string Path)> Removed { get; } = [];

    public Task ApplyAsync(string userName, string path, PathAccess access, bool applyDefault, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw FileManagerException.NotSupported("acl unavailable");
        }

        Applied.Add((userName, path, access, applyDefault));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AclEntry>> GetForUserAsync(string userName, string path, CancellationToken cancellationToken = default)
    {
        var entries = Applied
            .Where(a => a.User == userName && a.Path == path)
            .Select(a => new AclEntry(userName, IPosixAclService.ToPermissions(a.Access), a.Default))
            .ToArray();

        return Task.FromResult<IReadOnlyList<AclEntry>>(entries);
    }

    public Task RemoveAsync(string userName, string path, bool removeDefault, CancellationToken cancellationToken = default)
    {
        Removed.Add((userName, path));
        return Task.CompletedTask;
    }
}

/// <summary>Yields host users from memory.</summary>
public sealed class StubUserDirectory : IHostUserDirectory
{
    private readonly List<HostUser> _users;

    public StubUserDirectory(params HostUser[] users)
    {
        _users = users.ToList();
    }

    public void Add(HostUser user) => _users.Add(user);

    public IReadOnlyList<HostUser> GetAll() => _users;

    public HostUser? Find(string userName) => _users.FirstOrDefault(u => u.Name == userName);

    public IReadOnlyList<GroupEntry> GetGroups() => [];

    public string ResolveUserName(uint uid) => _users.FirstOrDefault(u => u.Uid == uid)?.Name ?? uid.ToString();

    public string ResolveGroupName(uint gid) => gid.ToString();

    public string GetPasswordHash(string userName) => _users.FirstOrDefault(u => u.Name == userName)?.HasUsablePassword == true ? "$6$deadbeef$hash" : string.Empty;

    public bool HasUsableAdminAccount() => _users.Any(u => u.IsAdmin && u.HasUsablePassword);

    public void Invalidate()
    {
    }
}

/// <summary>Scratch directory with fixture account files.</summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fm-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(string relative) => System.IO.Path.Combine(Root, relative.TrimStart('/'));

    public string Write(string relative, string content)
    {
        var path = Path(relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Directory(string relative)
    {
        var path = Path(relative);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Root))
            {
                System.IO.Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }
}

public static class TestOptions
{
    public static IOptions<FileManagerOptions> Create(Action<FileManagerOptions>? configure = null)
    {
        var options = new FileManagerOptions
        {
            BrowseRoots = ["/"],
            AdminGroups = ["sudo", "wheel"],
            AllowNonRootDev = true,
            Impersonation = new ImpersonationOptions { Enabled = false },
            Bootstrap = new BootstrapOptions { Enabled = false },
        };

        configure?.Invoke(options);
        return Options.Create(options);
    }

    public static LinuxHostUserDirectory CreateDirectory(FileManagerOptions options) =>
        new(Options.Create(options), NullLogger<LinuxHostUserDirectory>.Instance);
}
