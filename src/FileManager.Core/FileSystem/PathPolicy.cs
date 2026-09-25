using FileManager.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileManager.Core.FileSystem;

public interface IPathPolicy
{
    IReadOnlyList<string> Roots { get; }

    /// <summary>Normalizes an absolute (or root relative) path and rejects anything outside the browse roots.</summary>
    string Resolve(string? path);

    string? GetParent(string path);
}

public sealed class PathPolicy : IPathPolicy
{
    public PathPolicy(IOptions<FileManagerOptions> options, ILogger<PathPolicy> logger)
    {
        var configured = options.Value.BrowseRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(NormalizeRoot)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(r => r.Length)
            .ToArray();

        if (configured.Length == 0)
        {
            // Requirement 2: browse the host filesystem with the logged in user's own permissions.
            logger.LogWarning(
                "FileManager:BrowseRoots is empty; defaulting to \"/\". Set the roots explicitly to limit navigation.");
            configured = ["/"];
        }

        Roots = configured;
    }

    public IReadOnlyList<string> Roots { get; }

    public string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Roots[0];
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw FileManagerException.BadRequest("Путь содержит недопустимый символ.");
        }

        string full;
        if (Path.IsPathRooted(path))
        {
            full = Path.GetFullPath(path);
        }
        else
        {
            full = Path.GetFullPath(Path.Combine(Roots[0], path));
        }

        full = TrimTrailingSeparator(full);

        foreach (var root in Roots)
        {
            if (IsInside(root, full))
            {
                return full;
            }
        }

        throw FileManagerException.Forbidden($"Путь {full} находится вне разрешённых корней.");
    }

    public string? GetParent(string path)
    {
        if (Roots.Contains(path, StringComparer.Ordinal))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
        {
            return null;
        }

        parent = TrimTrailingSeparator(Path.GetFullPath(parent));
        return Roots.Any(root => IsInside(root, parent)) ? parent : null;
    }

    private static bool IsInside(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = root.EndsWith('/') ? root : root + "/";
        return candidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root.Trim());
        return TrimTrailingSeparator(full);
    }

    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length > 1 && path.EndsWith('/'))
        {
            return path.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/";
        }

        return path;
    }
}
