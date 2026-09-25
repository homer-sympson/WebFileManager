using FileManager.Core.Configuration;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Linux;

public sealed record AclEntry(string UserName, string Permissions, bool IsDefault);

public interface IPosixAclService
{
    bool IsAvailable { get; }

    Task ApplyAsync(string userName, string path, PathAccess access, bool applyDefault, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AclEntry>> GetForUserAsync(string userName, string path, CancellationToken cancellationToken = default);

    Task RemoveAsync(string userName, string path, bool removeDefault, CancellationToken cancellationToken = default);

    static string ToPermissions(PathAccess access) => access switch
    {
        PathAccess.Read => "r-X",
        _ => "rwX",
    };
}

/// <summary>
/// POSIX ACLs are the native Linux way to grant a single account access to an existing directory
/// without changing its owner or group, which is exactly what requirement 4 asks for.
/// </summary>
public sealed class PosixAclService : IPosixAclService
{
    private readonly IProcessRunner _runner;
    private readonly FileManagerOptions _options;
    private readonly ILogger<PosixAclService> _logger;

    public PosixAclService(IProcessRunner runner, IOptions<FileManagerOptions> options, ILogger<PosixAclService> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
        IsAvailable = _options.Acl.Enabled && _runner.Exists(_options.Acl.SetFacl) && _runner.Exists(_options.Acl.GetFacl);
    }

    public bool IsAvailable { get; }

    public async Task ApplyAsync(string userName, string path, PathAccess access, bool applyDefault, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var permissions = IPosixAclService.ToPermissions(access);
        var result = await _runner.RunAsync(
            _options.Acl.SetFacl,
            ["-m", $"u:{userName}:{permissions}", path],
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, $"Не удалось выдать права ACL на {path}");

        if (applyDefault && _options.Acl.DefaultAclOnDirectories && Directory.Exists(path))
        {
            var defaultResult = await _runner.RunAsync(
                _options.Acl.SetFacl,
                ["-m", $"d:u:{userName}:{permissions}", path],
                null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(defaultResult, $"Не удалось выдать default ACL на {path}");
        }

        _logger.LogInformation("ACL {Permissions} applied for {User} on {Path}.", permissions, userName, path);
    }

    public async Task<IReadOnlyList<AclEntry>> GetForUserAsync(string userName, string path, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var result = await _runner.RunAsync(_options.Acl.GetFacl, ["-cp", path], null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return [];
        }

        var entries = new List<AclEntry>();
        foreach (var rawLine in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine;
            var isDefault = false;
            if (line.StartsWith("default:", StringComparison.Ordinal))
            {
                isDefault = true;
                line = line["default:".Length..];
            }

            var parts = line.Split(':');
            if (parts.Length != 3 || !parts[0].Equals("user", StringComparison.Ordinal) || parts[1].Length == 0)
            {
                continue;
            }

            if (!parts[1].Equals(userName, StringComparison.Ordinal))
            {
                continue;
            }

            entries.Add(new AclEntry(parts[1], parts[2], isDefault));
        }

        return entries;
    }

    public async Task RemoveAsync(string userName, string path, bool removeDefault, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var result = await _runner.RunAsync(_options.Acl.SetFacl, ["-x", $"u:{userName}", path], null, cancellationToken).ConfigureAwait(false);
        if (!result.Success && !result.CombinedOutput.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Removing ACL for {User} on {Path} failed: {Output}", userName, path, result.CombinedOutput.Trim());
        }

        if (removeDefault && Directory.Exists(path))
        {
            await _runner.RunAsync(_options.Acl.SetFacl, ["-x", $"d:u:{userName}", path], null, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw FileManagerException.NotSupported(
                "Утилиты setfacl/getfacl недоступны. Установите пакет 'acl', чтобы управлять правами доступа к каталогам.");
        }
    }

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.Success)
        {
            return;
        }

        var output = result.CombinedOutput.Trim();
        if (output.Contains("not supported", StringComparison.OrdinalIgnoreCase))
        {
            throw FileManagerException.Conflict($"{message}: файловая система не поддерживает ACL. {output}");
        }

        throw FileManagerException.Conflict($"{message}: {output}");
    }
}
