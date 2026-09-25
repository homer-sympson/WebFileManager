using FileManager.Core.Linux;

namespace FileManager.Core.Tests;

public class AccountFileParserTests
{
    private const string Passwd = """
        root:x:0:0:root:/root:/bin/bash
        daemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin
        alice:x:1000:1000:Alice Example:/home/alice:/bin/bash
        bob:x:1001:1001:Bob:/home/bob:/bin/bash
        carol:x:1002:1002:Carol:/home/carol:/usr/sbin/nologin
        admin:x:1004:1004:Admin:/home/admin:/bin/bash
        """;

    private const string Group = """
        root:x:0:
        sudo:x:27:admin
        wheel:x:10:
        alice:x:1000:
        bob:x:1001:
        carol:x:1002:
        admin:x:1004:
        """;

    private const string Shadow = """
        root:*:19000:0:99999:7:::
        daemon:*:19000:0:99999:7:::
        alice:$6$abcdefgh$L2RkWuRRaXbSPKE2h075RMaIsfsrCKiKdR4CAmWFZdxb7FD5ntuy3JlinIGZEpWPL2s7jolhp7uEtLBxC/4Xq1:19000:0:99999:7:::
        bob:!:19000:0:99999:7:::
        carol::19000:0:99999:7:::
        admin:$6$abcdefgh$L2RkWuRRaXbSPKE2h075RMaIsfsrCKiKdR4CAmWFZdxb7FD5ntuy3JlinIGZEpWPL2s7jolhp7uEtLBxC/4Xq1:19000:0:99999:7:::
        """;

    [Fact]
    public void ParsesPasswdFields()
    {
        var entries = AccountFileParser.ParsePasswd(Passwd);

        Assert.Equal(6, entries.Count);
        var alice = entries.Single(e => e.Name == "alice");
        Assert.Equal(1000u, alice.Uid);
        Assert.Equal(1000u, alice.Gid);
        Assert.Equal("Alice Example", alice.Gecos);
        Assert.Equal("/home/alice", alice.Home);
        Assert.Equal("/bin/bash", alice.Shell);
    }

    [Fact]
    public void ParsesGroupMembers()
    {
        var groups = AccountFileParser.ParseGroup(Group);

        Assert.Equal(27u, groups.Single(g => g.Name == "sudo").Gid);
        Assert.Equal(["admin"], groups.Single(g => g.Name == "sudo").Members);
        Assert.Empty(groups.Single(g => g.Name == "wheel").Members);
    }

    [Fact]
    public void ParsesShadowHashes()
    {
        var shadow = AccountFileParser.ParseShadow(Shadow);

        Assert.StartsWith("$6$", shadow["alice"], StringComparison.Ordinal);
        Assert.Equal("!", shadow["bob"]);
        Assert.Equal(string.Empty, shadow["carol"]);
    }

    [Theory]
    [InlineData("$6$abcdefgh$hash", true)]
    [InlineData("$y$j9T$hash", true)]
    [InlineData("", false)]
    [InlineData("*", false)]
    [InlineData("!", false)]
    [InlineData("!!", false)]
    [InlineData("!$6$abcdefgh$hash", false)]
    [InlineData(null, false)]
    public void DetectsUsablePasswordHash(string? hash, bool expected) =>
        Assert.Equal(expected, AccountFileParser.IsUsablePasswordHash(hash));

    [Fact]
    public void BuildUsersFiltersSystemAndPasswordlessAccounts()
    {
        var users = AccountFileParser.BuildUsers(
            AccountFileParser.ParsePasswd(Passwd),
            AccountFileParser.ParseGroup(Group),
            AccountFileParser.ParseShadow(Shadow),
            ["sudo", "wheel"],
            minimumUid: 1000,
            includeSystemUsers: false);

        Assert.DoesNotContain(users, u => u.Name == "daemon");
        Assert.Contains(users, u => u.Name == "root");

        var alice = users.Single(u => u.Name == "alice");
        Assert.True(alice.HasUsablePassword);
        Assert.False(alice.IsAdmin);

        Assert.False(users.Single(u => u.Name == "bob").HasUsablePassword);
        Assert.False(users.Single(u => u.Name == "carol").HasUsablePassword);

        var admin = users.Single(u => u.Name == "admin");
        Assert.True(admin.IsAdmin);
        Assert.True(admin.HasUsablePassword);

        Assert.True(users.Single(u => u.Name == "root").IsAdmin);
    }

    [Fact]
    public void IdentityCarriesSupplementaryGroups()
    {
        var users = AccountFileParser.BuildUsers(
            AccountFileParser.ParsePasswd(Passwd),
            AccountFileParser.ParseGroup(Group),
            AccountFileParser.ParseShadow(Shadow),
            ["sudo", "wheel"],
            minimumUid: 1000,
            includeSystemUsers: false);

        var identity = users.Single(u => u.Name == "admin").ToIdentity();

        Assert.Equal(1004u, identity.Uid);
        Assert.Contains(27u, identity.SupplementaryGroups);
        Assert.True(identity.IsAdmin);
    }

    [Fact]
    public void IncludeSystemUsersKeepsServiceAccounts()
    {
        var users = AccountFileParser.BuildUsers(
            AccountFileParser.ParsePasswd(Passwd),
            AccountFileParser.ParseGroup(Group),
            AccountFileParser.ParseShadow(Shadow),
            ["sudo"],
            minimumUid: 1000,
            includeSystemUsers: true);

        Assert.Contains(users, u => u.Name == "daemon");
    }

    [Fact]
    public void IgnoresCommentAndPlusLines()
    {
        var entries = AccountFileParser.ParsePasswd("""
            # comment
            +nisgroup

            alice:x:1000:1000:A:/home/alice:/bin/bash
            """);

        Assert.Single(entries);
        Assert.Equal("alice", entries[0].Name);
    }
}
