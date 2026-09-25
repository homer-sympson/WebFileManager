using FileManager.Core.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Data;

/// <summary>Applies schema changes at startup, waiting for the database to accept connections.</summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly IDbContextFactory<FileManagerDbContext> _factory;
    private readonly DatabaseOptions _options;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        IDbContextFactory<FileManagerDbContext> factory,
        IOptions<DatabaseOptions> options,
        ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.MigrateOnStartup)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, _options.ConnectRetrySeconds));
        var delay = TimeSpan.FromSeconds(1);
        Exception? last = null;

        while (true)
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                if (_options.Provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation("Database schema is up to date (provider {Provider}).", _options.Provider);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (DateTime.UtcNow >= deadline)
                {
                    throw new FileManagerException(HttpStatus.InternalServerError, "Не удалось подготовить базу данных.", ex);
                }

                _logger.LogWarning("Database is not ready yet ({Message}); retrying in {Delay}.", ex.Message, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
            }
        }

        // Unreachable, but keeps the compiler happy about the loop above.
        throw new FileManagerException(HttpStatus.InternalServerError, "Не удалось подготовить базу данных.", last);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
