using FileManager.Core.Configuration;
using FileManager.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FileManager.Api.Tests;

/// <summary>
/// Boots the real API with SQLite, fixture account files and impersonation disabled so the whole
/// HTTP surface can be exercised without Docker, PostgreSQL or root-only side effects.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private const string Secret123Hash = "$6$abcdefgh$L2RkWuRRaXbSPKE2h075RMaIsfsrCKiKdR4CAmWFZdxb7FD5ntuy3JlinIGZEpWPL2s7jolhp7uEtLBxC/4Xq1";

    public ApiFactory()
    {
        Root = Path.Combine(Path.GetTempPath(), "fm-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Share = EnsureDirectory("share");
        EnsureDirectory("share/sub");
        EnsureDirectory("sudoers.d");
        EnsureDirectory("staging");
        EnsureDirectory("home");

        PasswdPath = WriteFile("etc/passwd", $"""
            root:x:0:0:root:/root:/bin/bash
            alice:x:1000:1000:Alice:{Share}:/bin/bash
            admin:x:1004:1004:Admin:{Share}:/bin/bash
            nopassword:x:1005:1005:No Password:{Share}:/bin/bash
            service:x:1006:1006:Service:{Share}:/usr/sbin/nologin
            """);

        GroupPath = WriteFile("etc/group", """
            root:x:0:
            sudo:x:27:admin
            wheel:x:10:
            alice:x:1000:
            admin:x:1004:
            nopassword:x:1005:
            service:x:1006:
            """);

        ShadowPath = WriteFile("etc/shadow", $"""
            root:*:19000:0:99999:7:::
            alice:{Secret123Hash}:19000:0:99999:7:::
            admin:{Secret123Hash}:19000:0:99999:7:::
            nopassword::19000:0:99999:7:::
            service:{Secret123Hash}:19000:0:99999:7:::
            """);

        WriteFile("home/marker.txt", "marker");
    }

    public string Root { get; }

    public string Share { get; }

    public string PasswdPath { get; }

    public string GroupPath { get; }

    public string ShadowPath { get; }

    public string SudoersDirectory => Path.Combine(Root, "sudoers.d");

    public string StagingDirectory => Path.Combine(Root, "staging");

    public string FilePath(string relative) => Path.Combine(Root, "share", relative);

    /// <summary>Opens the application database so tests can assert on persisted state.</summary>
    public FileManagerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<FileManagerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Root, "filemanager-tests.db")}")
            .Options;

        return new FileManagerDbContext(options);
    }

    public string WriteShareFile(string relative, string content)
    {
        var path = FilePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string EnsureDirectory(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // PostConfigure wins over appsettings.json bindings, which matters for list valued options
        // such as BrowseRoots (configuration providers merge array indexes instead of replacing them).
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<DatabaseOptions>(options =>
            {
                options.Provider = "sqlite";
                options.ConnectionString = $"Data Source={Path.Combine(Root, "filemanager-tests.db")}";
                options.MigrateOnStartup = true;
                options.ConnectRetrySeconds = 5;
            });

            services.PostConfigure<FileManagerOptions>(options =>
            {
                options.BrowseRoots = [Share];
                options.AllowNonRootDev = true;
                options.Impersonation = new ImpersonationOptions { Enabled = false };
                options.Bootstrap = new BootstrapOptions { Enabled = false };
                options.Auth = new AuthOptions
                {
                    Provider = "shadow",
                    AllowDevelopmentBypass = true,
                    ExposeUserList = true,
                    MaxFailedAttempts = 50,
                    PageCloseGraceSeconds = 1,
                };
                options.Linux = new LinuxPathsOptions
                {
                    Passwd = PasswdPath,
                    Shadow = ShadowPath,
                    Group = GroupPath,
                    SudoersDir = SudoersDirectory,
                    Visudo = "true",
                    AccountCacheSeconds = 0,
                };
                options.Upload = new UploadOptions { TempRoot = StagingDirectory };
            });
        });
    }

    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.EndsWith('\n') ? content : content + Environment.NewLine);
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // leftover temp files are harmless
            }
        }
    }
}
