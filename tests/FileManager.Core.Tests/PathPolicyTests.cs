using FileManager.Core.Configuration;
using FileManager.Core.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests;

public class PathPolicyTests
{
    private static IPathPolicy Create(params string[] roots) =>
        new PathPolicy(TestOptions.Create(o => o.BrowseRoots = [.. roots]), NullLogger<PathPolicy>.Instance);

    [Fact]
    public void ResolvesRelativePathsAgainstThePrimaryRoot()
    {
        var policy = Create("/srv/data");

        Assert.Equal("/srv/data", policy.Resolve(null));
        Assert.Equal("/srv/data/sub", policy.Resolve("sub"));
        Assert.Equal("/srv/data/sub", policy.Resolve("/srv/data/sub"));
    }

    [Fact]
    public void CollapsesTraversalBeforeCheckingRoots()
    {
        var policy = Create("/srv/data");

        Assert.Equal("/srv/data/other", policy.Resolve("/srv/data/sub/../other"));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/srv/other")]
    [InlineData("/srv/data/../../etc/passwd")]
    [InlineData("/")]
    public void RejectsPathsOutsideRoots(string path)
    {
        var policy = Create("/srv/data");

        var exception = Assert.Throws<FileManagerException>(() => policy.Resolve(path));
        Assert.Equal(HttpStatus.Forbidden, exception.StatusCode);
    }

    [Fact]
    public void RejectsNullBytes()
    {
        var policy = Create("/srv/data");

        Assert.Throws<FileManagerException>(() => policy.Resolve("/srv/data/a\0b"));
    }

    [Fact]
    public void NormalizesRootWithTrailingSlash()
    {
        var policy = Create("/srv/data/");

        Assert.Equal("/srv/data", policy.Roots[0]);
        Assert.Equal("/srv/data/x", policy.Resolve("/srv/data/x"));
    }

    [Fact]
    public void SupportsMultipleRootsAndParentLookup()
    {
        var policy = Create("/srv/data", "/mnt/share");

        Assert.Equal("/mnt/share/a", policy.Resolve("/mnt/share/a"));
        Assert.Equal("/srv/data", policy.GetParent("/srv/data/sub"));
        Assert.Null(policy.GetParent("/srv/data"));
        Assert.Equal("/mnt/share", policy.GetParent("/mnt/share/x"));
    }

    [Fact]
    public void EmptyConfigurationFallsBackToTheFilesystemRoot()
    {
        var policy = new PathPolicy(TestOptions.Create(o => o.BrowseRoots = []), NullLogger<PathPolicy>.Instance);

        Assert.Equal(["/"], policy.Roots);
        Assert.Equal("/etc", policy.Resolve("/etc"));
    }
}
