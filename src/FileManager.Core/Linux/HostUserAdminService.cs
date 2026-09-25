using System.Text;
using FileManager.Core.Configuration;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Linux;

public sealed record CreateHostUserRequest(
    string UserName,
    string Password,
    string? FullName,
    string? Shell,
    bool CreateHome,
    string? HomeDirectory,
    IReadOnlyList<string> Groups,
    bool GrantSudo,
    bool SudoNopasswd,
    IReadOnlyList<PathGrant> PathGrants);

public sealed record UpdateHostUserRequest(
    string? FullName,
    string? Shell,
    string? Password,
    IReadOnlyList<string>? Groups,
    bool? GrantSudo,
    bool? SudoNopasswd,
    IReadOnlyList<PathGrant>? PathGrants,
    IReadOnlyList<string>? RevokeAclPaths);

public sealed record HostUserDetails(
    string Name,
    uint Uid,
    uint Gid,
    string FullName,
    string HomeDirectory,
    string Shell,
    IReadOnlyList<string> Groups,
    bool IsAdmin,
    bool HasUsablePassword,
    bool HasSudoRule,
    string? SudoRule,
    IReadOnlyList<PathAccessInfo> Access,
    string? SudoersPath);

public sealed record PathAccessInfo(string Path, IReadOnlyList<AclEntry> Entries, string? Error);

public interface IHostUserAdminService
{
    IReadOnlyList<string> GetExistingGroups();

    Task<HostUserDetails> GetAsync(string userName, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task<HostUserDetails> CreateAsync(CreateHostUserRequest request, CancellationToken cancellationToken = default);

    Task<HostUserDetails> UpdateAsync(string userName, UpdateHostUserRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(string userName, bool removeHome, IReadOnlyList<string> revokeAclPaths, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates and maintains host accounts. All settings live in the operating system itself:
/// /etc/passwd + /etc/shadow (account), /etc/group (membership) and /etc/sudoers.d (privileges).
/// </summary>
public sealed class HostUserAdminService : IHostUserAdminService
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "root", "daemon", "bin", "sys", "sync", "games", "man", "lp", "mail", "news", "uucp",
        "proxy", "www-data", "backup", "list", "irc", "gnats", "nobody", "systemd-network",
        "systemd-timesync", "messagebus", "sshd", "postgres",
    };

    private readonly IProcessRunner _runner;
    private readonly IHostUserDirectory _directory;
    private readonly ISudoersService _sudoers;
    private readonly IPosixAclService _acl;
    private readonly FileManagerOptions _options;
    private readonly ILogger<HostUserAdminService> _logger;

    public HostUserAdminService(
        IProcessRunner runner,
        IHostUserDirectory directory,
        ISudoersService sudoers,
        IPosixAclService acl,
        IOptions<FileManagerOptions> options,
        ILogger<HostUserAdminService> logger)
    {
        _runner = runner;
        _directory = directory;
        _sudoers = sudoers;
        _acl = acl;
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyList<string> GetExistingGroups() =>
        _directory.GetGroups().Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    public async Task<HostUserDetails> GetAsync(string userName, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var user = _directory.Find(userName) ?? throw FileManagerException.NotFound($"Пользователь {userName} не найден.");
        return await BuildDetailsAsync(user, paths, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HostUserDetails> CreateAsync(CreateHostUserRequest request, CancellationToken cancellationToken = default)
    {
        ValidateNewUser(request);

        if (_directory.Find(request.UserName) is not null)
        {
            throw FileManagerException.Conflict($"Пользователь {request.UserName} уже существует.");
        }

        var shell = string.IsNullOrWhiteSpace(request.Shell) ? "/bin/bash" : request.Shell;
        var effectiveGroups = WithAdminGroup(request.Groups, request.GrantSudo);
        var createdGroups = new List<string>();
        var appliedAcl = new List<string>();
        var userCreated = false;

        try
        {
            foreach (var group in effectiveGroups)
            {
                if (_directory.GetGroups().Any(g => g.Name == group))
                {
                    continue;
                }

                await RunOrThrowAsync(_options.Linux.GroupAdd, [group], null, cancellationToken).ConfigureAwait(false);
                createdGroups.Add(group);
            }

            var arguments = new List<string> { "-m", "-U", "-s", shell };
            if (!string.IsNullOrWhiteSpace(request.FullName))
            {
                arguments.Add("-c");
                arguments.Add(request.FullName);
            }

            if (!string.IsNullOrWhiteSpace(request.HomeDirectory))
            {
                arguments.Add("-d");
                arguments.Add(request.HomeDirectory);
            }

            if (!request.CreateHome)
            {
                arguments.Remove("-m");
            }

            arguments.Add(request.UserName);
            await RunOrThrowAsync(_options.Linux.UserAdd, arguments, null, cancellationToken).ConfigureAwait(false);
            userCreated = true;

            await SetPasswordAsync(request.UserName, request.Password, cancellationToken).ConfigureAwait(false);

            if (effectiveGroups.Count > 0)
            {
                await RunOrThrowAsync(_options.Linux.UserMod, ["-aG", string.Join(',', effectiveGroups), request.UserName], null, cancellationToken).ConfigureAwait(false);
            }

            if (request.GrantSudo)
            {
                await _sudoers.ApplyAsync(request.UserName, request.SudoNopasswd, cancellationToken).ConfigureAwait(false);
            }

            foreach (var grant in request.PathGrants)
            {
                await ApplyGrantAsync(request.UserName, grant, cancellationToken).ConfigureAwait(false);
                appliedAcl.Add(grant.Path);
            }

            _directory.Invalidate();
            _logger.LogInformation("Host user {User} created.", request.UserName);
            var created = _directory.Find(request.UserName)
                ?? throw FileManagerException.Failed($"Пользователь {request.UserName} создан, но не найден в базе пользователей.");
            return await BuildDetailsAsync(created, request.PathGrants.Select(g => g.Path).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating host user {User} failed; rolling back.", request.UserName);
            await RollbackCreateAsync(request.UserName, userCreated, appliedAcl, createdGroups, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<HostUserDetails> UpdateAsync(string userName, UpdateHostUserRequest request, CancellationToken cancellationToken = default)
    {
        var user = _directory.Find(userName) ?? throw FileManagerException.NotFound($"Пользователь {userName} не найден.");

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            await SetPasswordAsync(userName, request.Password, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(request.FullName))
        {
            await RunOrThrowAsync(_options.Linux.UserMod, ["-c", request.FullName, userName], null, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(request.Shell))
        {
            await RunOrThrowAsync(_options.Linux.UserMod, ["-s", request.Shell, userName], null, cancellationToken).ConfigureAwait(false);
        }

        if (request.Groups is { } groups)
        {
            var sanitized = WithAdminGroup(groups, request.GrantSudo ?? false)
                .Where(g => g.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (sanitized.Length > 0)
            {
                await RunOrThrowAsync(_options.Linux.UserMod, ["-G", string.Join(',', sanitized), userName], null, cancellationToken).ConfigureAwait(false);
            }
        }

        if (request.GrantSudo is { } grantSudo)
        {
            if (grantSudo)
            {
                await _sudoers.ApplyAsync(userName, request.SudoNopasswd ?? false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _sudoers.RemoveAsync(userName, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (request.SudoNopasswd is { } nopasswd && _sudoers.Read(userName) is not null)
        {
            await _sudoers.ApplyAsync(userName, nopasswd, cancellationToken).ConfigureAwait(false);
        }

        if (request.RevokeAclPaths is { } revoke)
        {
            foreach (var path in revoke)
            {
                await _acl.RemoveAsync(userName, path, true, cancellationToken).ConfigureAwait(false);
            }
        }

        if (request.PathGrants is { } grants)
        {
            foreach (var grant in grants)
            {
                await ApplyGrantAsync(userName, grant, cancellationToken).ConfigureAwait(false);
            }
        }

        _directory.Invalidate();
        var refreshed = _directory.Find(userName) ?? user;
        var paths = request.PathGrants?.Select(g => g.Path).ToArray() ?? [];
        return await BuildDetailsAsync(refreshed, paths, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string userName, bool removeHome, IReadOnlyList<string> revokeAclPaths, CancellationToken cancellationToken = default)
    {
        if (!UserNameValidator.IsValid(userName))
        {
            throw FileManagerException.BadRequest("Недопустимое имя пользователя.");
        }

        var user = _directory.Find(userName) ?? throw FileManagerException.NotFound($"Пользователь {userName} не найден.");
        if (user.Uid == 0)
        {
            throw FileManagerException.Forbidden("Удаление root-аккаунта запрещено.");
        }

        await _sudoers.RemoveAsync(userName, cancellationToken).ConfigureAwait(false);

        foreach (var path in revokeAclPaths)
        {
            if (_acl.IsAvailable)
            {
                await _acl.RemoveAsync(userName, path, true, cancellationToken).ConfigureAwait(false);
            }
        }

        var arguments = new List<string>();
        if (removeHome)
        {
            arguments.Add("-r");
        }

        arguments.Add(userName);
        await RunOrThrowAsync(_options.Linux.UserDel, arguments, null, cancellationToken).ConfigureAwait(false);
        _directory.Invalidate();
        _logger.LogInformation("Host user {User} deleted (removeHome={RemoveHome}).", userName, removeHome);
    }

    /// <summary>
    /// Sudo rights are expressed twice on Linux: the sudoers rule grants the privilege and the admin
    /// group membership is what this application (and common tooling) uses to recognise an admin.
    /// </summary>
    private IReadOnlyList<string> WithAdminGroup(IReadOnlyList<string> groups, bool grantSudo)
    {
        if (!grantSudo)
        {
            return groups;
        }

        var adminGroup = _options.AdminGroups.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
        if (adminGroup is null || groups.Contains(adminGroup, StringComparer.Ordinal))
        {
            return groups;
        }

        return [.. groups, adminGroup];
    }

    private async Task<HostUserDetails> BuildDetailsAsync(HostUser user, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var access = new List<PathAccessInfo>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!_acl.IsAvailable)
            {
                access.Add(new PathAccessInfo(path, [], "setfacl/getfacl недоступны (пакет acl не установлен)."));
                continue;
            }

            try
            {
                access.Add(new PathAccessInfo(path, await _acl.GetForUserAsync(user.Name, path, cancellationToken).ConfigureAwait(false), null));
            }
            catch (FileManagerException ex)
            {
                access.Add(new PathAccessInfo(path, [], ex.Message));
            }
        }

        var rule = _sudoers.Read(user.Name);
        return new HostUserDetails(
            user.Name,
            user.Uid,
            user.Gid,
            user.FullName,
            user.HomeDirectory,
            user.Shell,
            user.GroupNames,
            user.IsAdmin,
            user.HasUsablePassword,
            rule is not null,
            rule,
            access,
            rule is null ? null : _sudoers.GetPath(user.Name));
    }

    private async Task ApplyGrantAsync(string userName, PathGrant grant, CancellationToken cancellationToken)
    {
        if (!Path.IsPathRooted(grant.Path))
        {
            throw FileManagerException.BadRequest($"Путь {grant.Path} должен быть абсолютным.");
        }

        if (!Directory.Exists(grant.Path) && !File.Exists(grant.Path))
        {
            throw FileManagerException.BadRequest($"Путь {grant.Path} не существует.");
        }

        await _acl.ApplyAsync(userName, grant.Path, grant.Access, grant.Default, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetPasswordAsync(string userName, string password, CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        var payload = $"{userName}:{password}{Environment.NewLine}";
        await RunOrThrowAsync(_options.Linux.ChPasswd, [], payload, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateNewUser(CreateHostUserRequest request)
    {
        if (!UserNameValidator.IsValid(request.UserName))
        {
            throw FileManagerException.BadRequest("Имя пользователя: до 32 символов, начинается с буквы или '_', содержит только a-z, 0-9, '_' и '-'.");
        }

        if (ReservedNames.Contains(request.UserName))
        {
            throw FileManagerException.BadRequest($"Имя {request.UserName} зарезервировано системой.");
        }

        ValidatePassword(request.Password);

        foreach (var group in request.Groups)
        {
            if (string.IsNullOrWhiteSpace(group) || group.Contains(':') || group.Contains(','))
            {
                throw FileManagerException.BadRequest($"Недопустимое имя группы: {group}");
            }
        }
    }

    private void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw FileManagerException.BadRequest("Пароль не может быть пустым: такие пользователи игнорируются при входе.");
        }

        if (password.Length < _options.Auth.MinPasswordLength)
        {
            throw FileManagerException.BadRequest($"Пароль должен быть не короче {_options.Auth.MinPasswordLength} символов.");
        }

        if (password.Contains(':') || password.Contains('\n') || password.Contains('\r'))
        {
            throw FileManagerException.BadRequest("Пароль не должен содержать ':' и переводы строк.");
        }
    }

    private async Task RunOrThrowAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(fileName, arguments, standardInput, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            return;
        }

        var output = result.CombinedOutput.Trim();
        throw FileManagerException.Conflict(string.IsNullOrEmpty(output) ? $"{fileName} завершился с кодом {result.ExitCode}." : output);
    }

    private async Task RollbackCreateAsync(
        string userName,
        bool userCreated,
        IReadOnlyList<string> appliedAcl,
        IReadOnlyList<string> createdGroups,
        CancellationToken cancellationToken)
    {
        try
        {
            if (appliedAcl.Count > 0 && _acl.IsAvailable)
            {
                foreach (var path in appliedAcl)
                {
                    await _acl.RemoveAsync(userName, path, true, cancellationToken).ConfigureAwait(false);
                }
            }

            await _sudoers.RemoveAsync(userName, cancellationToken).ConfigureAwait(false);

            if (userCreated)
            {
                await _runner.RunAsync(_options.Linux.UserDel, ["-r", userName], null, cancellationToken).ConfigureAwait(false);
            }

            foreach (var group in createdGroups)
            {
                await _runner.RunAsync(_options.Linux.GroupDel, [group], null, cancellationToken).ConfigureAwait(false);
            }

            _directory.Invalidate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback after failed user creation for {User} did not complete cleanly.", userName);
        }
    }

    internal static string PasswordPayload(string userName, string password)
    {
        var builder = new StringBuilder(userName);
        builder.Append(':').Append(password).Append('\n');
        return builder.ToString();
    }
}
