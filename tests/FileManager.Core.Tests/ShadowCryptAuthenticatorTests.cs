using FileManager.Core.Linux;

namespace FileManager.Core.Tests;

public class ShadowCryptAuthenticatorTests
{
    // Real SHA-512 crypt hash of "secret123" with salt "abcdefgh" (openssl passwd -6 -salt abcdefgh secret123).
    private const string Secret123Hash = "$6$abcdefgh$L2RkWuRRaXbSPKE2h075RMaIsfsrCKiKdR4CAmWFZdxb7FD5ntuy3JlinIGZEpWPL2s7jolhp7uEtLBxC/4Xq1";

    [Theory]
    [InlineData("secret123", true)]
    [InlineData("secret124", false)]
    [InlineData("Secret123", false)]
    [InlineData("", false)]
    public void VerifiesPasswordAgainstRealShadowHash(string password, bool expected)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var outcome = ShadowCryptAuthenticator.Verify("alice", password, Secret123Hash);

        Assert.Equal(expected, outcome.Success);
        Assert.Equal(ShadowCryptAuthenticator.ProviderName, outcome.Provider);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("!")]
    [InlineData("!!")]
    [InlineData("!$6$abcdefgh$hash")]
    public void RefusesAccountsWithoutUsablePassword(string hash)
    {
        var outcome = ShadowCryptAuthenticator.Verify("bob", "secret123", hash);

        Assert.False(outcome.Success);
        Assert.Contains("не задан пароль", outcome.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticatorReadsHashFromDirectory()
    {
        var directory = new StubUserDirectory(HostUserFactory.Create("alice", 1000, admin: false, hasPassword: true));

        // The stub reports a placeholder hash, so authentication must fail rather than throw.
        var authenticator = new ShadowCryptAuthenticator(directory);
        var outcome = await authenticator.AuthenticateAsync("alice", "whatever", CancellationToken.None);

        Assert.False(outcome.Success);
    }
}

public static class HostUserFactory
{
    public static FileManager.Core.Models.HostUser Create(
        string name,
        uint uid,
        bool admin,
        bool hasPassword,
        uint gid = 0,
        string home = "/home/user",
        string shell = "/bin/bash") =>
        new(
            name,
            uid,
            gid == 0 ? uid : gid,
            name,
            home,
            shell,
            admin ? ["sudo"] : [name],
            uid == 0 ? [0u] : [uid],
            admin,
            hasPassword);
}
