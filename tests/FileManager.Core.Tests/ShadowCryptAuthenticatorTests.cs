using FileManager.Core.Linux;

namespace FileManager.Core.Tests;

public class ShadowCryptAuthenticatorTests
{
    // Real SHA-512 crypt hash of "secret123" with salt "abcdefgh" (openssl passwd -6 -salt abcdefgh secret123).
    private const string Secret123Hash = "$6$abcdefgh$L2RkWuRRaXbSPKE2h075RMaIsfsrCKiKdR4CAmWFZdxb7FD5ntuy3JlinIGZEpWPL2s7jolhp7uEtLBxC/4Xq1";

    // Real yescrypt hash of "secret123" written by `chpasswd` on Debian 12 (the default method there).
    // Verification must not depend on PAM for this format.
    private const string YescryptSecret123Hash = "$y$j9T$9aPxgIcm5j.Ym2N3qnGmL0$5zPw.lxYIoqbNA/w6FXPHdVqmKuLFh42eHqXBGZ5Su4";

    private static ShadowEntry Entry(string hash, long lastChangeDays = -1, long maxDays = -1, long inactiveDays = -1, long expireDays = -1) =>
        new(hash, lastChangeDays, -1, maxDays, -1, inactiveDays, expireDays);

    private static long DaysAgo(int days) => (long)(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalDays - days;

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

        var outcome = ShadowCryptAuthenticator.Verify("alice", password, Entry(Secret123Hash));

        Assert.Equal(expected, outcome.Success);
        Assert.Equal(ShadowCryptAuthenticator.ProviderName, outcome.Provider);
    }

    [Fact]
    public void VerifiesAYescryptHashProducedByChpasswd()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // `chpasswd` on Debian writes yescrypt ($y$) by default; crypt_r(3) must handle it because
        // the authenticator no longer depends on PAM for verification (PAM failed in containers).
        Assert.True(ShadowCryptAuthenticator.Verify("alice", "secret123", Entry(YescryptSecret123Hash)).Success);
        Assert.False(ShadowCryptAuthenticator.Verify("alice", "secret124", Entry(YescryptSecret123Hash)).Success);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("!")]
    [InlineData("!!")]
    [InlineData("!$6$abcdefgh$hash")]
    public void RefusesAccountsWithoutUsablePassword(string hash)
    {
        var outcome = ShadowCryptAuthenticator.Verify("bob", "secret123", Entry(hash));

        Assert.False(outcome.Success);
        Assert.Contains("не задан пароль", outcome.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesAnAccountWhoseShadowEntryIsMissing()
    {
        var outcome = ShadowCryptAuthenticator.Verify("ghost", "secret123", null);

        Assert.False(outcome.Success);
        Assert.Contains("отсутствует", outcome.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesAnExpiredAccount()
    {
        var outcome = ShadowCryptAuthenticator.Verify(
            "alice",
            "secret123",
            Entry(Secret123Hash, expireDays: DaysAgo(1)));

        Assert.False(outcome.Success);
        Assert.Contains("учётной записи истёк", outcome.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesAnExpiredPassword()
    {
        var outcome = ShadowCryptAuthenticator.Verify(
            "alice",
            "secret123",
            Entry(Secret123Hash, lastChangeDays: DaysAgo(120), maxDays: 90));

        Assert.False(outcome.Success);
        Assert.Contains("пароля истёк", outcome.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptsAFreshPassword()
    {
        var outcome = ShadowCryptAuthenticator.Verify(
            "alice",
            "secret123",
            Entry(Secret123Hash, lastChangeDays: DaysAgo(5), maxDays: 90, expireDays: (long)(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalDays + 365));

        Assert.True(outcome.Success);
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
