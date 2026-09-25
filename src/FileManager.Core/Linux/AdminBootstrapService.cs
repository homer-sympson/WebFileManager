using FileManager.Core.Configuration;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Linux;

public sealed record BootstrapResult(bool Performed, string Message);

public interface IAdminBootstrapService
{
    Task<BootstrapResult> EnsureAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Requirement 5: when the host has no usable root level account (uid 0 or admin group member with a
/// password), create one so the application stays reachable.
/// </summary>
public sealed class AdminBootstrapService : IAdminBootstrapService
{
    private readonly IHostUserDirectory _directory;
    private readonly IHostUserAdminService _admin;
    private readonly FileManagerOptions _options;
    private readonly ILogger<AdminBootstrapService> _logger;

    public AdminBootstrapService(
        IHostUserDirectory directory,
        IHostUserAdminService admin,
        IOptions<FileManagerOptions> options,
        ILogger<AdminBootstrapService> logger)
    {
        _directory = directory;
        _admin = admin;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<BootstrapResult> EnsureAsync(CancellationToken cancellationToken = default)
    {
        var bootstrap = _options.Bootstrap;
        if (!bootstrap.Enabled)
        {
            return new BootstrapResult(false, "Bootstrap отключён настройкой FileManager:Bootstrap:Enabled=false.");
        }

        _directory.Invalidate();
        if (_directory.HasUsableAdminAccount())
        {
            return new BootstrapResult(false, "В системе есть root-аккаунт с заданным паролем.");
        }

        if (string.IsNullOrEmpty(bootstrap.AdminPassword))
        {
            throw FileManagerException.Failed(
                "В системе нет ни одного root-аккаунта с паролем, а пароль для аварийного администратора не задан " +
                "(FileManager:Bootstrap:AdminPassword). Запуск остановлен, чтобы не потерять доступ.");
        }

        var existing = _directory.Find(bootstrap.AdminUser);
        if (existing is not null)
        {
            _logger.LogWarning(
                "No usable root account found; repairing existing account {User} (setting password and sudo rule).",
                bootstrap.AdminUser);

            await _admin.UpdateAsync(
                bootstrap.AdminUser,
                new UpdateHostUserRequest(
                    FullName: null,
                    Shell: null,
                    Password: bootstrap.AdminPassword,
                    Groups: existing.GroupNames,
                    GrantSudo: bootstrap.GrantSudo,
                    SudoNopasswd: bootstrap.SudoNopasswd,
                    PathGrants: null,
                    RevokeAclPaths: null),
                cancellationToken).ConfigureAwait(false);

            return new BootstrapResult(true, $"Аккаунт {bootstrap.AdminUser} обновлён: задан пароль и права sudo.");
        }

        _logger.LogWarning(
            "No usable root account found on this host; creating emergency administrator {User} with sudo rights.",
            bootstrap.AdminUser);

        await _admin.CreateAsync(
            new CreateHostUserRequest(
                UserName: bootstrap.AdminUser,
                Password: bootstrap.AdminPassword,
                FullName: "FileManager administrator",
                Shell: "/bin/bash",
                CreateHome: true,
                HomeDirectory: null,
                Groups: [],
                GrantSudo: bootstrap.GrantSudo,
                SudoNopasswd: bootstrap.SudoNopasswd,
                PathGrants: []),
            cancellationToken).ConfigureAwait(false);

        return new BootstrapResult(true, $"Создан аварийный администратор {bootstrap.AdminUser} с правами sudo.");
    }
}

/// <summary>Runs the bootstrap check once the application host has started.</summary>
public sealed class AdminBootstrapHostedService : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly IAdminBootstrapService _bootstrap;
    private readonly ILogger<AdminBootstrapHostedService> _logger;

    public AdminBootstrapHostedService(IAdminBootstrapService bootstrap, ILogger<AdminBootstrapHostedService> logger)
    {
        _bootstrap = bootstrap;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _bootstrap.EnsureAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Admin bootstrap: {Message}", result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin bootstrap failed.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
