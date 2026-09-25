using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Configuration;

/// <summary>
/// Applies the effective defaults for collection valued options after configuration binding.
/// The binder appends to existing collection instances, so the option classes deliberately start
/// empty and the defaults live here, where they cannot leak into deployment overrides.
/// </summary>
public sealed class FileManagerOptionsDefaults : IPostConfigureOptions<FileManagerOptions>
{
    public static readonly string[] DefaultAdminGroups = ["sudo", "wheel"];

    public static readonly string[] DefaultBrowseRoots = ["/"];

    private readonly ILogger<FileManagerOptionsDefaults> _logger;

    public FileManagerOptionsDefaults(ILogger<FileManagerOptionsDefaults> logger)
    {
        _logger = logger;
    }

    public void PostConfigure(string? name, FileManagerOptions options)
    {
        if (options.BrowseRoots.Count == 0)
        {
            _logger.LogWarning(
                "FileManager:BrowseRoots is not configured; defaulting to \"/\". Set it explicitly to limit navigation.");
            options.BrowseRoots.AddRange(DefaultBrowseRoots);
        }

        if (options.AdminGroups.Count == 0)
        {
            options.AdminGroups.AddRange(DefaultAdminGroups);
        }
    }
}
