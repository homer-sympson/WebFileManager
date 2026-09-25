using FileManager.Core.Models;

namespace FileManager.Api.Contracts;

public sealed record LoginRequest(string UserName, string Password);

public sealed record LoginOptionsResponse(bool ExposeUserList, IReadOnlyList<string> Users);

public sealed record UserProfileResponse(
    string UserName,
    uint Uid,
    uint Gid,
    string FullName,
    string HomeDirectory,
    bool IsAdmin,
    bool HasSudoRule,
    IReadOnlyList<string> Groups);

public sealed record CapabilitiesResponse(
    string Os,
    bool IsPrivileged,
    bool ImpersonationEnabled,
    bool PamAvailable,
    bool ShadowAvailable,
    bool AclAvailable,
    bool DatabaseReady,
    IReadOnlyList<string> BrowseRoots);

public sealed record DirectoryListingResponse(
    string Path,
    string? Parent,
    IReadOnlyList<FileEntry> Entries,
    bool Truncated,
    int Total);

public sealed record MkdirRequest(string Path);

public sealed record RenameRequest(string Path, string NewName);

public sealed record TransferRequest(IReadOnlyList<string> Sources, string Destination, ConflictPolicy Conflict);

public sealed record PathGrantRequest(string Path, PathAccess Access, bool Default);

public sealed record CreateUserRequest(
    string UserName,
    string Password,
    string? FullName,
    string? Shell,
    bool CreateHome,
    string? HomeDirectory,
    IReadOnlyList<string>? Groups,
    bool GrantSudo,
    bool SudoNopasswd,
    IReadOnlyList<PathGrantRequest>? PathGrants);

public sealed record UpdateUserRequest(
    string? FullName,
    string? Shell,
    string? Password,
    IReadOnlyList<string>? Groups,
    bool? GrantSudo,
    bool? SudoNopasswd,
    IReadOnlyList<PathGrantRequest>? PathGrants,
    IReadOnlyList<string>? RevokeAclPaths);

public sealed record HistoryRequest(string Path);

public sealed record HostUserResponse(
    string UserName,
    uint Uid,
    uint Gid,
    string FullName,
    string HomeDirectory,
    string Shell,
    IReadOnlyList<string> Groups,
    bool IsAdmin,
    bool HasPassword);

public sealed record HostUserListResponse(IReadOnlyList<HostUserResponse> Users, bool AclAvailable);
