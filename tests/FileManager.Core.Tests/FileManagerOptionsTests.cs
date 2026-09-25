using FileManager.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Tests;

/// <summary>
/// Guards the configuration binder trap: collection options that carry a default initializer keep the
/// default even when every deployment provider overrides the list.
/// </summary>
public class FileManagerOptionsTests
{
    [Fact]
    public void EmptyConfigurationAppliesEffectiveDefaults()
    {
        var options = Resolve(new Dictionary<string, string?>());

        Assert.Equal(["/"], options.BrowseRoots);
        Assert.Equal(["sudo", "wheel"], options.AdminGroups);
    }

    [Fact]
    public void ConfiguredArraysReplaceTheDefaultsCompletely()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["FileManager:BrowseRoots:0"] = "/data",
            ["FileManager:BrowseRoots:1"] = "/home",
            ["FileManager:AdminGroups:0"] = "sysadmin",
        });

        Assert.Equal(["/data", "/home"], options.BrowseRoots);
        Assert.Equal(["sysadmin"], options.AdminGroups);
    }

    [Fact]
    public void PartialOverrideDoesNotResurrectDefaults()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["FileManager:BrowseRoots:0"] = "/srv/files",
        });

        Assert.Equal(["/srv/files"], options.BrowseRoots);
    }

    private static FileManagerOptions Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        services.AddOptions<FileManagerOptions>().Bind(configuration.GetSection(FileManagerOptions.SectionName));
        services.AddSingleton<IPostConfigureOptions<FileManagerOptions>, FileManagerOptionsDefaults>();

        return services.BuildServiceProvider().GetRequiredService<IOptions<FileManagerOptions>>().Value;
    }
}
