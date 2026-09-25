using FileManager.Core;
using FileManager.Core.Linux;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests;

public class SudoersServiceTests
{
    [Fact]
    public void BuildsSudoersRule()
    {
        Assert.Equal(
            $"# Managed by FileManager. Do not edit manually.{Environment.NewLine}a-admin ALL=(ALL:ALL) ALL{Environment.NewLine}",
            SudoersService.BuildContent("a-admin", nopasswd: false));

        Assert.Contains("NOPASSWD: ALL", SudoersService.BuildContent("a-admin", nopasswd: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesValidatedDropInWithRestrictiveMode()
    {
        using var workspace = new TempWorkspace();
        var sudoersDir = workspace.Directory("sudoers.d");
        var runner = new FakeProcessRunner();
        var options = TestOptions.Create(o => o.Linux.SudoersDir = sudoersDir);
        var service = new SudoersService(runner, options, NullLogger<SudoersService>.Instance);

        await service.ApplyAsync("alice", nopasswd: false);

        var path = Path.Combine(sudoersDir, "alice");
        Assert.True(File.Exists(path));
        Assert.Contains("alice ALL=(ALL:ALL) ALL", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

        var validation = runner.Single("visudo");
        Assert.NotNull(validation);
        Assert.True(validation!.HasArgument("-cf"));
        Assert.Contains(path, validation.Arguments);

        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead, mode);
    }

    [Fact]
    public async Task RemovesDropInWhenVisudoRejectsIt()
    {
        using var workspace = new TempWorkspace();
        var sudoersDir = workspace.Directory("sudoers.d");
        var runner = new FakeProcessRunner
        {
            Handler = (file, _, _) => file == "visudo"
                ? new ProcessResult(1, string.Empty, "syntax error")
                : new ProcessResult(0, string.Empty, string.Empty),
        };

        var options = TestOptions.Create(o => o.Linux.SudoersDir = sudoersDir);
        var service = new SudoersService(runner, options, NullLogger<SudoersService>.Instance);

        var exception = await Assert.ThrowsAsync<FileManagerException>(() => service.ApplyAsync("alice", nopasswd: true));

        Assert.Equal(HttpStatus.BadRequest, exception.StatusCode);
        Assert.False(File.Exists(Path.Combine(sudoersDir, "alice")));
    }

    [Theory]
    [InlineData("Alice")]
    [InlineData("ali ce")]
    [InlineData("alice;rm -rf /")]
    [InlineData("")]
    [InlineData("1alice")]
    public async Task RejectsUnsafeUserNames(string userName)
    {
        using var workspace = new TempWorkspace();
        var options = TestOptions.Create(o => o.Linux.SudoersDir = workspace.Directory("sudoers.d"));
        var service = new SudoersService(new FakeProcessRunner(), options, NullLogger<SudoersService>.Instance);

        await Assert.ThrowsAsync<FileManagerException>(() => service.ApplyAsync(userName, nopasswd: true));
    }

    [Theory]
    [InlineData("alice", true)]
    [InlineData("a-admin", true)]
    [InlineData("user_1-x", true)]
    [InlineData("Alice", false)]
    [InlineData("-alice", false)]
    public void ValidatesUserNames(string name, bool expected) =>
        Assert.Equal(expected, UserNameValidator.IsValid(name));
}
