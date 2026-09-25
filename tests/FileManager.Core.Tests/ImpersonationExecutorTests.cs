using FileManager.Core;
using FileManager.Core.Configuration;
using FileManager.Core.FileSystem;
using FileManager.Core.Interop;
using FileManager.Core.Linux;
using FileManager.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests;

/// <summary>
/// Exercises the real setresuid/setresgid/setgroups path. Runs only as root on Linux (the CI/development
/// container here is root), and touches no account files.
/// </summary>
public class ImpersonationExecutorTests
{
    private const uint NobodyUid = 65534;
    private const uint NobodyGid = 65534;

    private static bool CanRun => OperatingSystem.IsLinux() && Environment.IsPrivilegedProcess;

    [Fact]
    public void StartupCheckRejectsProcessWideCredentialSwitching()
    {
        if (!CanRun)
        {
            return;
        }

        using var executor = CreateExecutor();

        if (executor.ProbeThreadIsolation())
        {
            // A normal Linux host with glibc >= 2.24: credentials are per thread, startup must succeed.
            executor.VerifyCredentialSwitch();
            return;
        }

        // Sandboxes that intercept setxid apply credentials to the whole process; the service must refuse
        // to start rather than serve one user's request with another user's identity.
        var exception = Assert.Throws<InvalidOperationException>(executor.VerifyCredentialSwitch);
        Assert.Contains("process-wide", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunsTheDelegateWithTheTargetCredentialsAndRestoresRoot()
    {
        if (!CanRun)
        {
            return;
        }

        using var executor = CreateExecutor();
        var identity = new LinuxIdentity(NobodyUid, NobodyGid, "nobody", [NobodyGid], false);

        var observed = await executor.RunAsync(identity, () => (Uid: GetEffectiveUid(), Gid: GetEffectiveGid()));

        Assert.Equal(NobodyUid, observed.Uid);
        Assert.Equal(NobodyGid, observed.Gid);
        // Back to root on the caller thread.
        Assert.Equal(0u, GetEffectiveUid());
        Assert.Equal(0u, GetEffectiveGid());
    }

    [Fact]
    public async Task FilesCreatedInsideTheScopeBelongToTheImpersonatedUser()
    {
        if (!CanRun)
        {
            return;
        }

        using var executor = CreateExecutor();
        var directory = Path.Combine(Path.GetTempPath(), $"fm-impersonation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        var identity = new LinuxIdentity(NobodyUid, NobodyGid, "nobody", [NobodyGid], false);
        var path = Path.Combine(directory, "as-nobody.txt");

        try
        {
            await executor.RunAsync(identity, () => File.WriteAllText(path, "written by nobody"));

            Assert.True(File.Exists(path));
            Assert.True(NativeFileStat.TryGetOwner(path, out var ownerUid, out _));
            Assert.Equal(NobodyUid, ownerUid);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AdminIdentitiesKeepTheRootCredentials()
    {
        if (!CanRun)
        {
            return;
        }

        using var executor = CreateExecutor();
        var admin = new LinuxIdentity(0, 0, "root", [0], true);

        var uid = await executor.RunAsync(admin, GetEffectiveUid);

        Assert.Equal(0u, uid);
    }

    [Fact]
    public async Task ExceptionsPropagateAndRootIsRestored()
    {
        if (!CanRun)
        {
            return;
        }

        using var executor = CreateExecutor();
        var identity = new LinuxIdentity(NobodyUid, NobodyGid, "nobody", [NobodyGid], false);

        await Assert.ThrowsAsync<FileManagerException>(async () =>
            await executor.RunAsync<object?>(identity, () => throw FileManagerException.Forbidden("nope")));

        Assert.Equal(0u, GetEffectiveUid());
    }

    [Fact]
    public async Task InlineExecutorIsUsedWhenImpersonationIsDisabled()
    {
        var inline = new InlineImpersonationExecutor();

        Assert.False(inline.IsEnabled);
        inline.VerifyCredentialSwitch();
        Assert.Equal(42, await inline.RunAsync(new LinuxIdentity(1, 1, "x", [1], false), () => 42));
    }

    private static LinuxImpersonationExecutor CreateExecutor() =>
        new(
            TestOptions.Create(o => o.Impersonation = new ImpersonationOptions
            {
                Enabled = true,
                Threads = 2,
                SelfTestUid = NobodyUid,
                SelfTestGid = NobodyGid,
            }),
            NullLogger<LinuxImpersonationExecutor>.Instance);

    private static uint GetEffectiveUid() => Libc.GetEUid();

    private static uint GetEffectiveGid() => Libc.GetEGid();
}
