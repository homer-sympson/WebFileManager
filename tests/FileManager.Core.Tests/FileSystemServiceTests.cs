using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FileManager.Core;
using FileManager.Core.Configuration;
using FileManager.Core.FileSystem;
using FileManager.Core.Linux;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileManager.Core.Tests;

public class FileSystemServiceTests
{
    private static readonly LinuxIdentity Admin = new(0, 0, "root", [0], true);
    private static readonly LinuxIdentity Alice = new(12345, 12345, "alice", [12345], false);

    [Fact]
    public async Task ListsDirectoriesFirstThenNames()
    {
        using var ctx = new ServiceContext();
        ctx.Write("b.txt", "b");
        ctx.Write("a.txt", "a");
        ctx.Directory("sub");
        File.SetUnixFileMode(ctx.Path("a.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var listing = await ctx.Service.ListAsync(Admin, ctx.Root);

        Assert.Equal(["sub", "a.txt", "b.txt"], listing.Entries.Select(e => e.Name));
        Assert.Equal(FileEntryType.Directory, listing.Entries[0].Type);
        Assert.Equal("drwxr-xr-x", listing.Entries[0].Mode.Substring(0, 10));
        Assert.StartsWith("-rw", listing.Entries[1].Mode, StringComparison.Ordinal);
        Assert.Null(listing.ParentPath);
        Assert.False(listing.Truncated);
        Assert.Equal(3, listing.TotalEntries);
    }

    [Fact]
    public async Task TruncatesLargeListings()
    {
        using var ctx = new ServiceContext(maxEntries: 2);
        ctx.Write("a.txt", "a");
        ctx.Write("b.txt", "b");
        ctx.Write("c.txt", "c");

        var listing = await ctx.Service.ListAsync(Admin, ctx.Root);

        Assert.Equal(2, listing.Entries.Count);
        Assert.True(listing.Truncated);
        Assert.Equal(3, listing.TotalEntries);
    }

    [Fact]
    public async Task ComputesEffectiveAccessFromModeAndIdentity()
    {
        using var ctx = new ServiceContext();
        ctx.Write("public.txt", "x");
        File.SetUnixFileMode(ctx.Path("public.txt"), UnixFileMode.UserRead | UnixFileMode.OtherRead);

        var adminListing = await ctx.Service.ListAsync(Admin, ctx.Root);
        var aliceListing = await ctx.Service.ListAsync(Alice, ctx.Root);

        Assert.Equal("rwx", adminListing.Entries[0].EffectiveAccess);
        Assert.Equal("r--", aliceListing.Entries[0].EffectiveAccess);
    }

    [Fact]
    public async Task RefusesPathsOutsideBrowseRoots()
    {
        using var ctx = new ServiceContext();

        var exception = await Assert.ThrowsAsync<FileManagerException>(() => ctx.Service.ListAsync(Admin, "/etc"));

        Assert.Equal(HttpStatus.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task CreatesAndRenamesDirectories()
    {
        using var ctx = new ServiceContext();

        await ctx.Service.CreateDirectoryAsync(Admin, ctx.Path("new"));
        Assert.True(Directory.Exists(ctx.Path("new")));

        await ctx.Service.RenameAsync(Admin, ctx.Path("new"), "renamed");
        Assert.True(Directory.Exists(ctx.Path("renamed")));
        Assert.False(Directory.Exists(ctx.Path("new")));

        await Assert.ThrowsAsync<FileManagerException>(() => ctx.Service.CreateDirectoryAsync(Admin, ctx.Path("renamed")));
        await Assert.ThrowsAsync<FileManagerException>(() => ctx.Service.RenameAsync(Admin, ctx.Path("renamed"), "bad/name"));
    }

    [Fact]
    public async Task RefusesToDeleteOrRenameTheBrowseRoot()
    {
        using var ctx = new ServiceContext();

        Assert.Equal(HttpStatus.Forbidden, (await Assert.ThrowsAsync<FileManagerException>(() => ctx.Service.DeleteAsync(Admin, ctx.Root, true))).StatusCode);
        Assert.Equal(HttpStatus.Forbidden, (await Assert.ThrowsAsync<FileManagerException>(() => ctx.Service.RenameAsync(Admin, ctx.Root, "other"))).StatusCode);
        Assert.Equal(HttpStatus.Forbidden, (await Assert.ThrowsAsync<FileManagerException>(() => ctx.Service.DeleteAsync(Admin, "/", true))).StatusCode);
    }

    [Fact]
    public async Task CopiesFilesWithEveryConflictPolicy()
    {
        using var ctx = new ServiceContext();
        ctx.Write("src.txt", "hello");
        ctx.Directory("dest");

        var copied = await ctx.Service.CopyAsync(Admin, [ctx.Path("src.txt")], ctx.Path("dest"), ConflictPolicy.Fail);
        Assert.Single(copied.Affected);
        Assert.Equal("hello", await File.ReadAllTextAsync(ctx.Path("dest/src.txt")));

        var conflict = await Assert.ThrowsAsync<FileManagerException>(() =>
            ctx.Service.CopyAsync(Admin, [ctx.Path("src.txt")], ctx.Path("dest"), ConflictPolicy.Fail));
        Assert.Equal(HttpStatus.Conflict, conflict.StatusCode);

        var skipped = await ctx.Service.CopyAsync(Admin, [ctx.Path("src.txt")], ctx.Path("dest"), ConflictPolicy.Skip);
        Assert.Empty(skipped.Affected);
        Assert.Single(skipped.Skipped);

        ctx.Write("src.txt", "updated");
        await ctx.Service.CopyAsync(Admin, [ctx.Path("src.txt")], ctx.Path("dest"), ConflictPolicy.Overwrite);
        Assert.Equal("updated", await File.ReadAllTextAsync(ctx.Path("dest/src.txt")));

        var renamed = await ctx.Service.CopyAsync(Admin, [ctx.Path("src.txt")], ctx.Path("dest"), ConflictPolicy.Rename);
        Assert.EndsWith("src (1).txt", renamed.Affected[0], StringComparison.Ordinal);
        Assert.True(File.Exists(ctx.Path("dest/src (1).txt")));
    }

    [Fact]
    public async Task CopyingADirectoryIntoItselfIsRejected()
    {
        using var ctx = new ServiceContext();
        ctx.Directory("tree/inner");

        var exception = await Assert.ThrowsAsync<FileManagerException>(() =>
            ctx.Service.CopyAsync(Admin, [ctx.Path("tree")], ctx.Path("tree/inner"), ConflictPolicy.Fail));

        Assert.Equal(HttpStatus.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task MovesFilesAndDirectories()
    {
        using var ctx = new ServiceContext();
        ctx.Write("file.txt", "x");
        ctx.Directory("tree/child");
        ctx.Write("tree/child/deep.txt", "y");
        ctx.Directory("target");

        await ctx.Service.MoveAsync(Admin, [ctx.Path("file.txt"), ctx.Path("tree")], ctx.Path("target"), ConflictPolicy.Fail);

        Assert.True(File.Exists(ctx.Path("target/file.txt")));
        Assert.True(File.Exists(ctx.Path("target/tree/child/deep.txt")));
        Assert.False(Directory.Exists(ctx.Path("tree")));
    }

    [Fact]
    public async Task DeletesRecursively()
    {
        using var ctx = new ServiceContext();
        ctx.Directory("tree/child");
        ctx.Write("tree/child/deep.txt", "y");

        await ctx.Service.DeleteAsync(Admin, ctx.Path("tree"), recursive: true);

        Assert.False(Directory.Exists(ctx.Path("tree")));
    }

    [Fact]
    public async Task RefusesNonRecursiveDeleteOfNonEmptyDirectory()
    {
        using var ctx = new ServiceContext();
        ctx.Directory("tree/child");

        await Assert.ThrowsAnyAsync<IOException>(() => ctx.Service.DeleteAsync(Admin, ctx.Path("tree"), recursive: false));
    }

    [Fact]
    public async Task OpensFilesForReading()
    {
        using var ctx = new ServiceContext();
        ctx.Write("read.txt", "content");

        await using var stream = await ctx.Service.OpenReadAsync(Admin, ctx.Path("read.txt"));
        using var reader = new StreamReader(stream);
        Assert.Equal("content", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task CommitsStagedUploads()
    {
        using var ctx = new ServiceContext();
        ctx.Directory("uploads");

        var staged = ctx.Service.CreateStagingFile();
        try
        {
            await using (var writer = new StreamWriter(staged.Content, Encoding.UTF8, 1024, leaveOpen: true))
            {
                await writer.WriteAsync("payload");
                await writer.FlushAsync();
            }

            var committed = await ctx.Service.CommitUploadAsync(Admin, ctx.Path("uploads/data.txt"), staged.Content, overwrite: false);

            Assert.Equal(ctx.Path("uploads/data.txt"), committed);
            Assert.Equal("payload", await File.ReadAllTextAsync(committed));

            await Assert.ThrowsAsync<FileManagerException>(() =>
                ctx.Service.CommitUploadAsync(Admin, ctx.Path("uploads/data.txt"), staged.Content, overwrite: false));

            Assert.Equal(ctx.Path("uploads/data.txt"), await ctx.Service.CommitUploadAsync(Admin, ctx.Path("uploads/data.txt"), staged.Content, overwrite: true));
        }
        finally
        {
            await staged.Content.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreatesParentDirectoriesForFolderUploads()
    {
        using var ctx = new ServiceContext();
        ctx.Directory("uploads");
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("deep"));

        var committed = await ctx.Service.CommitUploadAsync(Admin, ctx.Path("uploads/a/b/c.txt"), content, overwrite: true, createParents: true);

        Assert.Equal("deep", await File.ReadAllTextAsync(committed));
    }

    [Fact]
    public async Task WritesTarGzArchives()
    {
        using var ctx = new ServiceContext();
        ctx.Directory("archive/nested");
        ctx.Write("archive/top.txt", "top");
        ctx.Write("archive/nested/deep.txt", "deep");

        var staged = ctx.Service.CreateStagingFile();
        try
        {
            await ctx.Service.WriteArchiveAsync(Admin, ctx.Path("archive"), staged.Content);
            await staged.Content.FlushAsync();
            staged.Content.Position = 0;

            using var gzip = new GZipStream(staged.Content, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var names = new List<string>();
            while (reader.GetNextEntry() is { } entry)
            {
                names.Add(entry.Name);
            }

            Assert.Contains("archive", names);
            Assert.Contains("archive/top.txt", names);
            Assert.Contains("archive/nested/deep.txt", names);
        }
        finally
        {
            await staged.Content.DisposeAsync();
        }
    }

    private sealed class ServiceContext : IDisposable
    {
        private readonly TempWorkspace _workspace = new();

        public ServiceContext(int maxEntries = 100)
        {
            Options = TestOptions.Create(o =>
            {
                o.BrowseRoots = [Root];
                o.Listing = new ListingOptions { MaxEntries = maxEntries };
                o.Upload = new UploadOptions { TempRoot = _workspace.Path("staging") };
            });

            var users = new StubUserDirectory(HostUserFactory.Create("alice", 12345, admin: false, hasPassword: true));
            Service = new FileSystemService(
                new InlineImpersonationExecutor(),
                new PathPolicy(Options, NullLogger<PathPolicy>.Instance),
                users,
                Options,
                NullLogger<FileSystemService>.Instance);
        }

        public string Root => _workspace.Root;

        public IOptions<FileManagerOptions> Options { get; }

        public FileSystemService Service { get; }

        public string Path(string relative) => _workspace.Path(relative);

        public string Write(string relative, string content) => _workspace.Write(relative, content);

        public string Directory(string relative) => _workspace.Directory(relative);

        public void Dispose() => _workspace.Dispose();
    }
}
