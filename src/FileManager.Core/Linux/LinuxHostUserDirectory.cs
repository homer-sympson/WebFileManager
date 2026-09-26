using FileManager.Core.Configuration;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Linux;

public interface IHostUserDirectory
{
    IReadOnlyList<HostUser> GetAll();

    HostUser? Find(string userName);

    IReadOnlyList<GroupEntry> GetGroups();

    string ResolveUserName(uint uid);

    string ResolveGroupName(uint gid);

    /// <summary>Raw /etc/shadow password field (hash) for the account, empty when unavailable.</summary>
    string GetPasswordHash(string userName);

    /// <summary>Full /etc/shadow entry (hash plus ageing fields), <c>null</c> when absent.</summary>
    ShadowEntry? GetShadowEntry(string userName);

    /// <summary>True when at least one uid 0 / admin group account can actually authenticate.</summary>
    bool HasUsableAdminAccount();

    void Invalidate();
}

/// <summary>Reads /etc/passwd, /etc/shadow and /etc/group with a short lived cache.</summary>
public sealed class LinuxHostUserDirectory : IHostUserDirectory
{
    private readonly FileManagerOptions _options;
    private readonly ILogger<LinuxHostUserDirectory> _logger;
    private readonly Lock _gate = new();
    private readonly TimeSpan _cacheLifetime;
    private Snapshot? _snapshot;

    public LinuxHostUserDirectory(IOptions<FileManagerOptions> options, ILogger<LinuxHostUserDirectory> logger)
    {
        _options = options.Value;
        _logger = logger;
        _cacheLifetime = TimeSpan.FromSeconds(Math.Max(0, _options.Linux.AccountCacheSeconds));
    }

    public IReadOnlyList<HostUser> GetAll() => Current().Users;

    public HostUser? Find(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        return Current().ByName.GetValueOrDefault(userName);
    }

    public IReadOnlyList<GroupEntry> GetGroups() => Current().Groups;

    public string ResolveUserName(uint uid) => Current().UserNames.GetValueOrDefault(uid, uid.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public string ResolveGroupName(uint gid) => Current().GroupNames.GetValueOrDefault(gid, gid.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public string GetPasswordHash(string userName) =>
        GetShadowEntry(userName)?.Hash ?? string.Empty;

    public ShadowEntry? GetShadowEntry(string userName) =>
        string.IsNullOrWhiteSpace(userName) ? null : Current().Shadow.GetValueOrDefault(userName);

    public bool HasUsableAdminAccount() => Current().Users.Any(u => u.IsAdmin && u.IsLoginCapable);

    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshot = null;
        }
    }

    private Snapshot Current()
    {
        lock (_gate)
        {
            if (_cacheLifetime > TimeSpan.Zero && _snapshot is { } cached && DateTime.UtcNow - cached.CreatedUtc < _cacheLifetime)
            {
                return cached;
            }

            _snapshot = Load();
            return _snapshot;
        }
    }

    private Snapshot Load()
    {
        var passwd = AccountFileParser.ParsePasswd(ReadFile(_options.Linux.Passwd));
        var groups = AccountFileParser.ParseGroup(ReadFile(_options.Linux.Group));
        var shadowEntries = AccountFileParser.ParseShadowEntries(ReadFile(_options.Linux.Shadow));
        var shadow = shadowEntries.ToDictionary(pair => pair.Key, pair => pair.Value.Hash, StringComparer.Ordinal);

        var users = AccountFileParser.BuildUsers(
            passwd,
            groups,
            shadow,
            _options.AdminGroups,
            _options.Auth.MinimumUid,
            _options.Auth.IncludeSystemUsers);

        return new Snapshot(
            DateTime.UtcNow,
            users,
            ByName(users),
            ByUid(users),
            passwd,
            groups,
            ByGid(groups),
            shadowEntries);
    }

    /// <summary>Duplicate keys are possible in hand edited account files; the first entry wins.</summary>
    private static Dictionary<string, HostUser> ByName(IEnumerable<HostUser> users)
    {
        var map = new Dictionary<string, HostUser>(StringComparer.Ordinal);
        foreach (var user in users)
        {
            map.TryAdd(user.Name, user);
        }

        return map;
    }

    private static Dictionary<uint, string> ByUid(IEnumerable<HostUser> users)
    {
        var map = new Dictionary<uint, string>();
        foreach (var user in users)
        {
            map.TryAdd(user.Uid, user.Name);
        }

        return map;
    }

    private static Dictionary<uint, string> ByGid(IEnumerable<GroupEntry> groups)
    {
        var map = new Dictionary<uint, string>();
        foreach (var group in groups)
        {
            map.TryAdd(group.Gid, group.Name);
        }

        return map;
    }

    private string ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot read {Path}; account information will be incomplete.", path);
            return string.Empty;
        }
    }

    private sealed record Snapshot(
        DateTime CreatedUtc,
        IReadOnlyList<HostUser> Users,
        IReadOnlyDictionary<string, HostUser> ByName,
        IReadOnlyDictionary<uint, string> UserNames,
        IReadOnlyList<PasswdEntry> Passwd,
        IReadOnlyList<GroupEntry> Groups,
        IReadOnlyDictionary<uint, string> GroupNames,
        IReadOnlyDictionary<string, ShadowEntry> Shadow);
}
