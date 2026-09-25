using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FileManager.Core.Configuration;
using FileManager.Core.Linux;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.FileSystem;

/// <summary>A file opened by the privileged process and handed to an impersonated worker or the response.</summary>
public sealed record StagedFile(Stream Content, string TempPath);

public interface IFileSystemService
{
    Task<DirectoryListing> ListAsync(LinuxIdentity identity, string path, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(LinuxIdentity identity, string path, CancellationToken cancellationToken = default);

    Task RenameAsync(LinuxIdentity identity, string path, string newName, CancellationToken cancellationToken = default);

    Task<TransferResult> CopyAsync(LinuxIdentity identity, IReadOnlyList<string> sources, string destination, ConflictPolicy policy, CancellationToken cancellationToken = default);

    Task<TransferResult> MoveAsync(LinuxIdentity identity, IReadOnlyList<string> sources, string destination, ConflictPolicy policy, CancellationToken cancellationToken = default);

    Task DeleteAsync(LinuxIdentity identity, string path, bool recursive, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(LinuxIdentity identity, string path, CancellationToken cancellationToken = default);

    /// <summary>Creates a staging file owned by the service account (never by the impersonated user).</summary>
    StagedFile CreateStagingFile();

    Task WriteArchiveAsync(LinuxIdentity identity, string sourcePath, Stream staging, CancellationToken cancellationToken = default);

    Task<string> CommitUploadAsync(LinuxIdentity identity, string destination, Stream staging, bool overwrite, bool createParents = false, CancellationToken cancellationToken = default);
}

public sealed class FileSystemService : IFileSystemService
{
    private const int CopyBufferSize = 1024 * 1024;

    private readonly IImpersonationExecutor _executor;
    private readonly IPathPolicy _paths;
    private readonly IHostUserDirectory _users;
    private readonly FileManagerOptions _options;
    private readonly ILogger<FileSystemService> _logger;

    public FileSystemService(
        IImpersonationExecutor executor,
        IPathPolicy paths,
        IHostUserDirectory users,
        IOptions<FileManagerOptions> options,
        ILogger<FileSystemService> logger)
    {
        _executor = executor;
        _paths = paths;
        _users = users;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DirectoryListing> ListAsync(LinuxIdentity identity, string path, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        return await _executor.RunAsync(identity, () => ListInternal(identity, resolved), cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(LinuxIdentity identity, string path, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        await _executor.RunAsync(identity, () =>
        {
            GuardNotRoot(resolved, "создавать каталоги");
            if (Directory.Exists(resolved) || File.Exists(resolved))
            {
                throw FileManagerException.Conflict($"Объект {resolved} уже существует.");
            }

            Directory.CreateDirectory(resolved);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(LinuxIdentity identity, string path, string newName, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        ValidateEntryName(newName);
        await _executor.RunAsync(identity, () =>
        {
            GuardNotRoot(resolved, "переименовывать");
            var parent = Path.GetDirectoryName(resolved) ?? throw FileManagerException.BadRequest("Нельзя переименовать корневой каталог.");
            var target = Path.Combine(parent, newName);
            _paths.Resolve(target);
            if (File.Exists(resolved) || Directory.Exists(resolved))
            {
                if (File.Exists(target) || Directory.Exists(target))
                {
                    throw FileManagerException.Conflict($"Объект {target} уже существует.");
                }

                MoveEntry(resolved, target);
            }
            else
            {
                throw FileManagerException.NotFound($"Объект {resolved} не найден.");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<TransferResult> CopyAsync(LinuxIdentity identity, IReadOnlyList<string> sources, string destination, ConflictPolicy policy, CancellationToken cancellationToken = default)
        => TransferAsync(identity, sources, destination, policy, move: false, cancellationToken);

    public Task<TransferResult> MoveAsync(LinuxIdentity identity, IReadOnlyList<string> sources, string destination, ConflictPolicy policy, CancellationToken cancellationToken = default)
        => TransferAsync(identity, sources, destination, policy, move: true, cancellationToken);

    public async Task DeleteAsync(LinuxIdentity identity, string path, bool recursive, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        await _executor.RunAsync(identity, () =>
        {
            GuardNotRoot(resolved, "удалять");
            DeleteEntry(resolved, recursive);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(LinuxIdentity identity, string path, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        return await _executor.RunAsync<Stream>(identity, () =>
        {
            if (Directory.Exists(resolved) && !IsSymlink(resolved))
            {
                throw FileManagerException.BadRequest("Каталог нельзя скачать как файл. Используйте архив.");
            }

            if (!File.Exists(resolved))
            {
                throw FileManagerException.NotFound($"Файл {resolved} не найден.");
            }

            return new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        }, cancellationToken).ConfigureAwait(false);
    }

    public StagedFile CreateStagingFile()
    {
        var root = _options.Upload.TempRoot;
        Directory.CreateDirectory(root);
        TrySetMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var tempPath = Path.Combine(root, $"fm-{Guid.NewGuid():N}.tmp");
        var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        TrySetMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new StagedFile(stream, tempPath);
    }

    public async Task WriteArchiveAsync(LinuxIdentity identity, string sourcePath, Stream staging, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(sourcePath);
        await _executor.RunAsync(identity, () =>
        {
            if (!Directory.Exists(resolved) && !File.Exists(resolved))
            {
                throw FileManagerException.NotFound($"Объект {resolved} не найден.");
            }

            staging.Position = 0;
            using var gzip = new GZipStream(staging, CompressionLevel.Fastest, leaveOpen: true);
            using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
            var baseName = Path.GetFileName(resolved.TrimEnd('/'));

            if (File.Exists(resolved) && !IsSymlink(resolved))
            {
                writer.WriteEntry(resolved, baseName);
                return;
            }

            if (IsSymlink(resolved))
            {
                writer.WriteEntry(resolved, baseName);
                return;
            }

            writer.WriteEntry(resolved, baseName);
            foreach (var entry in EnumerateTree(resolved))
            {
                writer.WriteEntry(entry.FullName, Path.Combine(baseName, entry.RelativePath));
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CommitUploadAsync(LinuxIdentity identity, string destination, Stream staging, bool overwrite, bool createParents = false, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(destination);
        return await _executor.RunAsync(identity, () =>
        {
            var parent = Path.GetDirectoryName(resolved);
            if (string.IsNullOrEmpty(parent))
            {
                throw FileManagerException.BadRequest("Некорректный путь назначения.");
            }

            if (!Directory.Exists(parent))
            {
                if (!createParents)
                {
                    throw FileManagerException.NotFound($"Каталог назначения {parent} не найден.");
                }

                Directory.CreateDirectory(parent);
            }

            if (Directory.Exists(resolved))
            {
                throw FileManagerException.Conflict($"По пути {resolved} уже существует каталог.");
            }

            if (File.Exists(resolved) && !overwrite)
            {
                throw FileManagerException.Conflict($"Файл {resolved} уже существует.");
            }

            var mode = File.Exists(resolved) ? GetMode(resolved) : null;
            staging.Position = 0;
            using (var target = new FileStream(resolved, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize))
            {
                staging.CopyTo(target, CopyBufferSize);
            }

            if (mode is { } existingMode)
            {
                TrySetMode(resolved, existingMode);
            }

            return resolved;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransferResult> TransferAsync(
        LinuxIdentity identity,
        IReadOnlyList<string> sources,
        string destination,
        ConflictPolicy policy,
        bool move,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            throw FileManagerException.BadRequest("Не указаны источники.");
        }

        var resolvedDestination = _paths.Resolve(destination);
        var resolvedSources = sources.Select(_paths.Resolve).ToArray();

        return await _executor.RunAsync(identity, () =>
        {
            if (!Directory.Exists(resolvedDestination))
            {
                throw FileManagerException.NotFound($"Каталог назначения {resolvedDestination} не найден.");
            }

            var affected = new List<string>();
            var skipped = new List<string>();

            foreach (var source in resolvedSources)
            {
                GuardNotRoot(source, move ? "перемещать" : "копировать");

                if (!File.Exists(source) && !Directory.Exists(source))
                {
                    throw FileManagerException.NotFound($"Объект {source} не найден.");
                }

                if (Directory.Exists(source) && !IsSymlink(source) &&
                    (resolvedDestination + "/").StartsWith(source + "/", StringComparison.Ordinal))
                {
                    throw FileManagerException.BadRequest("Нельзя копировать каталог внутрь самого себя.");
                }

                var target = Path.Combine(resolvedDestination, Path.GetFileName(source.TrimEnd('/')));
                var finalTarget = ResolveTarget(target, source, policy, skipped);
                if (finalTarget is null)
                {
                    continue;
                }

                if (move)
                {
                    MoveEntry(source, finalTarget);
                }
                else
                {
                    CopyEntry(source, finalTarget);
                }

                affected.Add(finalTarget);
            }

            return new TransferResult(affected, skipped);
        }, cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveTarget(string target, string source, ConflictPolicy policy, List<string> skipped)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            return target;
        }

        if (PathsEqual(target, source))
        {
            return policy switch
            {
                ConflictPolicy.Skip => Skip(skipped, target),
                ConflictPolicy.Fail => throw FileManagerException.Conflict($"Объект {target} уже существует."),
                _ => FreeName(target),
            };
        }

        return policy switch
        {
            ConflictPolicy.Fail => throw FileManagerException.Conflict($"Объект {target} уже существует."),
            ConflictPolicy.Skip => Skip(skipped, target),
            ConflictPolicy.Overwrite => Overwrite(target),
            ConflictPolicy.Rename => FreeName(target),
            _ => throw FileManagerException.BadRequest("Неизвестная политика конфликтов."),
        };
    }

    private static string? Skip(List<string> skipped, string target)
    {
        skipped.Add(target);
        return null;
    }

    private static string Overwrite(string target)
    {
        DeleteEntry(target, recursive: true);
        return target;
    }

    private static string FreeName(string target)
    {
        var directory = Path.GetDirectoryName(target) ?? "/";
        var name = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw FileManagerException.Conflict($"Не удалось подобрать свободное имя для {target}.");
    }

    private DirectoryListing ListInternal(LinuxIdentity identity, string resolved)
    {
        if (!Directory.Exists(resolved))
        {
            throw FileManagerException.NotFound($"Каталог {resolved} не найден.");
        }

        if (IsSymlink(resolved) && !Directory.Exists(resolved))
        {
            throw FileManagerException.BadRequest($"{resolved} не является каталогом.");
        }

        var max = _options.Listing.MaxEntries;
        var entries = new List<FileEntry>();
        var total = 0;

        foreach (var info in new DirectoryInfo(resolved).EnumerateFileSystemInfos())
        {
            total++;
            if (entries.Count >= max)
            {
                continue;
            }

            try
            {
                entries.Add(ToEntry(identity, info));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Skipping unreadable entry {Path}.", info.FullName);
            }
        }

        entries.Sort(static (a, b) =>
        {
            var aDir = a.Type == FileEntryType.Directory;
            var bDir = b.Type == FileEntryType.Directory;
            if (aDir != bDir)
            {
                return aDir ? -1 : 1;
            }

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new DirectoryListing(
            resolved,
            _paths.GetParent(resolved),
            entries,
            total > entries.Count,
            total);
    }

    private FileEntry ToEntry(LinuxIdentity identity, FileSystemInfo info)
    {
        var linkTarget = TryGetLinkTarget(info);
        var type = linkTarget is not null
            ? FileEntryType.Symlink
            : info is DirectoryInfo
                ? FileEntryType.Directory
                : info is FileInfo
                    ? FileEntryType.File
                    : FileEntryType.Other;

        long size = 0;
        DateTimeOffset modified = default;
        try
        {
            modified = info.LastWriteTimeUtc;
            if (info is FileInfo file)
            {
                size = file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // keep defaults for entries we cannot stat
        }

        var mode = GetMode(info.FullName);
        var hasOwner = NativeFileStat.TryGetOwner(info.FullName, out var uid, out var gid);
        var owner = hasOwner ? _users.ResolveUserName(uid) : string.Empty;
        var group = hasOwner ? _users.ResolveGroupName(gid) : string.Empty;

        return new FileEntry(
            info.Name,
            info.FullName,
            type,
            size,
            modified,
            FormatMode(type, mode),
            owner,
            group,
            linkTarget,
            EffectiveAccess(identity, hasOwner ? uid : null, hasOwner ? gid : null, mode));
    }

    /// <summary>What the logged in user may actually do with the entry (ignoring ACLs).</summary>
    internal static string EffectiveAccess(LinuxIdentity identity, uint? ownerUid, uint? ownerGid, UnixFileMode? mode)
    {
        if (identity.IsAdmin || identity.IsRoot)
        {
            return "rwx";
        }

        if (mode is not { } value)
        {
            return "?";
        }

        UnixFileMode read;
        UnixFileMode write;
        UnixFileMode execute;

        if (ownerUid is { } uid && uid == identity.Uid)
        {
            read = UnixFileMode.UserRead;
            write = UnixFileMode.UserWrite;
            execute = UnixFileMode.UserExecute;
        }
        else if (ownerGid is { } gid && identity.SupplementaryGroups.Contains(gid))
        {
            read = UnixFileMode.GroupRead;
            write = UnixFileMode.GroupWrite;
            execute = UnixFileMode.GroupExecute;
        }
        else
        {
            read = UnixFileMode.OtherRead;
            write = UnixFileMode.OtherWrite;
            execute = UnixFileMode.OtherExecute;
        }

        var builder = new StringBuilder(3);
        builder.Append(value.HasFlag(read) ? 'r' : '-');
        builder.Append(value.HasFlag(write) ? 'w' : '-');
        builder.Append(value.HasFlag(execute) ? 'x' : '-');
        return builder.ToString();
    }

    private static string? TryGetLinkTarget(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static UnixFileMode? GetMode(string path)
    {
        try
        {
            return File.GetUnixFileMode(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static void TrySetMode(string path, UnixFileMode mode)
    {
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // best effort
        }
    }

    internal static string FormatMode(FileEntryType type, UnixFileMode? mode)
    {
        var prefix = type switch
        {
            FileEntryType.Directory => 'd',
            FileEntryType.Symlink => 'l',
            _ => '-',
        };

        if (mode is not { } value)
        {
            return $"{prefix}?????????";
        }

        var builder = new StringBuilder(10);
        builder.Append(prefix);
        Append(builder, value, UnixFileMode.UserRead, 'r');
        Append(builder, value, UnixFileMode.UserWrite, 'w');
        Append(builder, value, UnixFileMode.UserExecute, 'x');
        Append(builder, value, UnixFileMode.GroupRead, 'r');
        Append(builder, value, UnixFileMode.GroupWrite, 'w');
        Append(builder, value, UnixFileMode.GroupExecute, 'x');
        Append(builder, value, UnixFileMode.OtherRead, 'r');
        Append(builder, value, UnixFileMode.OtherWrite, 'w');
        Append(builder, value, UnixFileMode.OtherExecute, 'x');
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, UnixFileMode mode, UnixFileMode flag, char symbol) =>
        builder.Append(mode.HasFlag(flag) ? symbol : '-');

    private static void DeleteEntry(string path, bool recursive)
    {
        if (IsSymlink(path))
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
            return;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        throw FileManagerException.NotFound($"Объект {path} не найден.");
    }

    private static void CopyEntry(string source, string target)
    {
        if (IsSymlink(source) || File.Exists(source) && !Directory.Exists(source))
        {
            var linkTarget = TryGetLinkTarget(new FileInfo(source));
            if (linkTarget is not null)
            {
                var parentDirectory = Path.GetDirectoryName(source);
                File.CreateSymbolicLink(target, parentDirectory is null ? linkTarget : MakeRelative(linkTarget, parentDirectory));
            }
            else
            {
                File.Copy(source, target, overwrite: false);
            }

            TryCopyMetadata(source, target);
            return;
        }

        Directory.CreateDirectory(target);
        TryCopyMetadata(source, target);

        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            CopyEntry(entry.FullName, Path.Combine(target, entry.Name));
        }
    }

    private static string MakeRelative(string linkTarget, string fromDirectory)
    {
        try
        {
            return Path.GetRelativePath(fromDirectory, Path.GetFullPath(linkTarget, fromDirectory));
        }
        catch (ArgumentException)
        {
            return linkTarget;
        }
    }

    private static void MoveEntry(string source, string target)
    {
        if (Directory.Exists(source) && !IsSymlink(source))
        {
            try
            {
                Directory.Move(source, target);
            }
            catch (IOException)
            {
                CopyEntry(source, target);
                Directory.Delete(source, recursive: true);
            }

            return;
        }

        try
        {
            File.Move(source, target, overwrite: false);
        }
        catch (IOException)
        {
            CopyEntry(source, target);
            File.Delete(source);
        }
    }

    private static void TryCopyMetadata(string source, string target)
    {
        try
        {
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // timestamps are best effort
        }

        if (GetMode(source) is { } mode)
        {
            TrySetMode(target, mode);
        }
    }

    private static IEnumerable<(string FullName, string RelativePath)> EnumerateTree(string root)
    {
        var rootInfo = new DirectoryInfo(root);
        foreach (var info in rootInfo.EnumerateFileSystemInfos())
        {
            var relative = Path.GetRelativePath(root, info.FullName);
            yield return (info.FullName, relative);
            if (info is DirectoryInfo directory && TryGetLinkTarget(directory) is null)
            {
                foreach (var nested in EnumerateTree(directory.FullName))
                {
                    yield return (nested.FullName, Path.Combine(relative, nested.RelativePath));
                }
            }
        }
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                return true;
            }

            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
        {
            throw FileManagerException.BadRequest("Недопустимое имя.");
        }

        if (name.Contains('\\', StringComparison.Ordinal))
        {
            throw FileManagerException.BadRequest("Недопустимое имя.");
        }
    }

    /// <summary>Protects the filesystem root and every configured browse root from destructive verbs.</summary>
    private void GuardNotRoot(string path, string action)
    {
        var isFileSystemRoot = path == "/" || Path.GetPathRoot(path) == path;
        var isBrowseRoot = _paths.GetParent(path) is null;

        if (isFileSystemRoot || isBrowseRoot)
        {
            throw FileManagerException.Forbidden($"Нельзя {action} корневой каталог.");
        }
    }
}
