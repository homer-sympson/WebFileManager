using FileManager.Core.Configuration;
using FileManager.Core.Data;
using FileManager.Core.FileSystem;
using FileManager.Core.Linux;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddFileManagerCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<FileManagerOptions>()
            .Bind(configuration.GetSection(FileManagerOptions.SectionName));
        services.AddSingleton<IPostConfigureOptions<FileManagerOptions>, FileManagerOptionsDefaults>();
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName));

        // Provider and connection string are resolved lazily so configuration sources added later
        // (for example by integration tests) are honoured.
        services.AddDbContextFactory<FileManagerDbContext>((provider, options) =>
        {
            var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            if (database.Provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
            {
                options.UseSqlite(string.IsNullOrWhiteSpace(database.ConnectionString) ? "Data Source=filemanager.db" : database.ConnectionString);
            }
            else
            {
                options.UseNpgsql(database.ConnectionString);
            }
        });

        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<IHostUserDirectory, LinuxHostUserDirectory>();
        services.TryAddSingleton<ISudoersService, SudoersService>();
        services.TryAddSingleton<IPosixAclService, PosixAclService>();
        services.TryAddSingleton<IHostUserAdminService, HostUserAdminService>();
        services.TryAddSingleton<IHostAuthenticator, HostAuthenticator>();
        services.TryAddSingleton<IAdminBootstrapService, AdminBootstrapService>();
        services.TryAddSingleton<IUserStore, UserStore>();
        services.TryAddSingleton<ISessionService, SessionService>();
        services.TryAddSingleton<IHistoryService, HistoryService>();
        services.TryAddSingleton<IAuditService, AuditService>();
        services.TryAddSingleton<IPathPolicy, PathPolicy>();
        services.TryAddSingleton<IFileSystemService, FileSystemService>();

        services.AddSingleton<IImpersonationExecutor>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<FileManagerOptions>>().Value;
            if (!options.Impersonation.Enabled || !OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
            {
                return new InlineImpersonationExecutor();
            }

            return ActivatorUtilities.CreateInstance<LinuxImpersonationExecutor>(provider);
        });

        services.AddHostedService<DatabaseInitializer>();
        services.AddHostedService<PrivilegeStartupCheck>();
        services.AddHostedService<AdminBootstrapHostedService>();

        return services;
    }
}

/// <summary>
/// Fails closed: the service refuses to start when it cannot act as other users, because that would
/// silently break requirement 2 (browsing with the logged in user's permissions).
/// </summary>
public sealed class PrivilegeStartupCheck : IHostedService
{
    private readonly IOptions<FileManagerOptions> _options;
    private readonly IImpersonationExecutor _executor;
    private readonly ILogger<PrivilegeStartupCheck> _logger;

    public PrivilegeStartupCheck(
        IOptions<FileManagerOptions> options,
        IImpersonationExecutor executor,
        ILogger<PrivilegeStartupCheck> logger)
    {
        _options = options;
        _executor = executor;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        if (!Environment.IsPrivilegedProcess)
        {
            if (options.AllowNonRootDev)
            {
                _logger.LogWarning(
                    "Running without root privileges. Filesystem operations will use the service account permissions only " +
                    "(FileManager:AllowNonRootDev=true).");
                return Task.CompletedTask;
            }

            throw new FileManagerException(
                HttpStatus.InternalServerError,
                "Сервис запущен без root-прав. Для работы с пользователями хоста и имперсонации нужен запуск от root " +
                "(для локальной отладки задайте FileManager:AllowNonRootDev=true).");
        }

        if (_executor.IsEnabled)
        {
            _executor.VerifyCredentialSwitch();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
