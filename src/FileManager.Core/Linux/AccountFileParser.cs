using FileManager.Core.Models;

namespace FileManager.Core.Linux;

public sealed record PasswdEntry(string Name, uint Uid, uint Gid, string Gecos, string Home, string Shell);

public sealed record GroupEntry(string Name, uint Gid, string[] Members);

/// <summary>
/// Pure parsing of the classic account files. Kept side effect free so it can be unit tested
/// against fixture content without touching the real host.
/// </summary>
public static class AccountFileParser
{
    public static IReadOnlyList<PasswdEntry> ParsePasswd(string content)
    {
        var result = new List<PasswdEntry>();
        foreach (var line in EnumerateLines(content))
        {
            var parts = line.Split(':');
            if (parts.Length < 7)
            {
                continue;
            }

            if (!uint.TryParse(parts[2], out var uid) || !uint.TryParse(parts[3], out var gid))
            {
                continue;
            }

            result.Add(new PasswdEntry(parts[0], uid, gid, parts[4], parts[5], parts[6]));
        }

        return result;
    }

    public static IReadOnlyList<GroupEntry> ParseGroup(string content)
    {
        var result = new List<GroupEntry>();
        foreach (var line in EnumerateLines(content))
        {
            var parts = line.Split(':');
            if (parts.Length < 4 || !uint.TryParse(parts[2], out var gid))
            {
                continue;
            }

            var members = parts[3].Length == 0
                ? []
                : parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            result.Add(new GroupEntry(parts[0], gid, members));
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> ParseShadow(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in EnumerateLines(content))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            var name = line[..idx];
            var rest = line[(idx + 1)..];
            var end = rest.IndexOf(':');
            var hash = end >= 0 ? rest[..end] : rest;
            result[name] = hash;
        }

        return result;
    }

    /// <summary>
    /// Login capable means: a real password hash is present. Empty, "*", "!!" and "!$6$..." are rejected
    /// (empty password or locked account), which is exactly requirement 3.
    /// </summary>
    public static bool IsUsablePasswordHash(string? hash) =>
        !string.IsNullOrEmpty(hash) && hash[0] == '$';

    public static bool IsNonLoginShell(string shell) => LoginShells.IsNonLogin(shell);

    public static IReadOnlyList<HostUser> BuildUsers(
        IReadOnlyList<PasswdEntry> passwd,
        IReadOnlyList<GroupEntry> groups,
        IReadOnlyDictionary<string, string> shadow,
        IReadOnlyCollection<string> adminGroups,
        uint minimumUid,
        bool includeSystemUsers)
    {
        var admin = new HashSet<string>(adminGroups, StringComparer.Ordinal);

        // Real hosts do contain several group names sharing one gid (for example nobody and nogroup
        // both use 65534): keep the first one instead of failing to parse /etc/group.
        var groupsByGid = new Dictionary<uint, string>();
        foreach (var group in groups)
        {
            groupsByGid.TryAdd(group.Gid, group.Name);
        }

        // user name -> group memberships (primary gid plus explicit membership)
        var memberships = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var groupIds = new Dictionary<string, List<uint>>(StringComparer.Ordinal);

        foreach (var entry in passwd)
        {
            memberships[entry.Name] = new List<string>();
            groupIds[entry.Name] = [entry.Gid];
            if (groupsByGid.TryGetValue(entry.Gid, out var primaryName))
            {
                memberships[entry.Name].Add(primaryName);
            }
        }

        foreach (var group in groups)
        {
            foreach (var member in group.Members)
            {
                if (!memberships.TryGetValue(member, out var list))
                {
                    continue; // group references an account that does not exist
                }

                list.Add(group.Name);
                if (!groupIds[member].Contains(group.Gid))
                {
                    groupIds[member].Add(group.Gid);
                }
            }
        }

        var users = new List<HostUser>();
        foreach (var entry in passwd)
        {
            if (!includeSystemUsers && entry.Uid < minimumUid && entry.Uid != 0)
            {
                continue;
            }

            var groupNames = memberships[entry.Name].Distinct(StringComparer.Ordinal).ToArray();
            var isAdmin = entry.Uid == 0 || groupNames.Any(admin.Contains);
            shadow.TryGetValue(entry.Name, out var hash);

            users.Add(new HostUser(
                entry.Name,
                entry.Uid,
                entry.Gid,
                entry.Gecos,
                entry.Home,
                entry.Shell,
                groupNames,
                groupIds[entry.Name].ToArray(),
                isAdmin,
                IsUsablePasswordHash(hash)));
        }

        return users;
    }

    private static IEnumerable<string> EnumerateLines(string content)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#' || line[0] == '+')
            {
                continue;
            }

            yield return line;
        }
    }
}
