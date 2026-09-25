namespace FileManager.Core.Models;

/// <summary>Identity a filesystem operation has to run as.</summary>
public sealed record LinuxIdentity(uint Uid, uint Gid, string UserName, uint[] SupplementaryGroups, bool IsAdmin)
{
    public bool IsRoot => Uid == 0;
}

/// <summary>A host account as described by /etc/passwd + /etc/shadow + /etc/group.</summary>
public sealed record HostUser(
    string Name,
    uint Uid,
    uint Gid,
    string FullName,
    string HomeDirectory,
    string Shell,
    string[] GroupNames,
    uint[] GroupIds,
    bool IsAdmin,
    bool HasUsablePassword)
{
    /// <summary>
    /// Requirement 3: only accounts that can actually log in are offered. Empty/locked passwords and
    /// nologin shells are ignored.
    /// </summary>
    public bool IsLoginCapable => HasUsablePassword && !LoginShells.IsNonLogin(Shell);

    public LinuxIdentity ToIdentity()
    {
        var groups = GroupIds.Length == 0 ? new[] { Gid } : GroupIds;
        return new LinuxIdentity(Uid, Gid, Name, groups, IsAdmin);
    }
}

/// <summary>Shells that mean "this account may not open an interactive session".</summary>
public static class LoginShells
{
    private static readonly HashSet<string> NonLogin = new(StringComparer.Ordinal)
    {
        "/usr/sbin/nologin",
        "/sbin/nologin",
        "/bin/false",
        "/usr/bin/false",
        "/bin/sync",
    };

    public static bool IsNonLogin(string shell) => shell.Length == 0 || NonLogin.Contains(shell);
}

public enum FileEntryType
{
    File,
    Directory,
    Symlink,
    Other,
}

public sealed record FileEntry(
    string Name,
    string FullPath,
    FileEntryType Type,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    string Mode,
    string Owner,
    string Group,
    string? LinkTarget,
    string EffectiveAccess);

public sealed record DirectoryListing(
    string Path,
    string? ParentPath,
    IReadOnlyList<FileEntry> Entries,
    bool Truncated,
    int TotalEntries);

public enum ConflictPolicy
{
    Fail,
    Overwrite,
    Skip,
    Rename,
}

public sealed record TransferResult(IReadOnlyList<string> Affected, IReadOnlyList<string> Skipped);

public enum PathAccess
{
    Read,
    Write,
    ReadWrite,
}

public sealed record PathGrant(string Path, PathAccess Access, bool Default);
