using FileManager.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FileManager.Api.Data;

/// <summary>Lets `dotnet ef migrations` work without a reachable PostgreSQL instance.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FileManagerDbContext>
{
    public FileManagerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FileManagerDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=filemanager;Username=filemanager;Password=filemanager")
            .Options;

        return new FileManagerDbContext(options);
    }
}
