namespace FileManager.Core.Configuration;

public sealed class FileManagerOptions
{
    public const string SectionName = "FileManager";

    /// <summary>
    /// Filesystem roots the API is willing to address. OS permissions still apply.
    /// Starts empty on purpose: the configuration binder appends to existing collections, so a
    /// non-empty default here would survive every deployment override. The effective default is
    /// applied by <see cref="FileManagerOptionsDefaults"/> after binding.
    /// </summary>
    public List<string> BrowseRoots { get; set; } = [];

    /// <summary>Groups whose members are treated as administrative (root level) users. Defaults after binding.</summary>
    public List<string> AdminGroups { get; set; } = [];

    /// <summary>Allows running as a non-root process for local development. Never enable in production.</summary>
    public bool AllowNonRootDev { get; set; }

    public ImpersonationOptions Impersonation { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

    public BootstrapOptions Bootstrap { get; set; } = new();

    public AclOptions Acl { get; set; } = new();

    public UploadOptions Upload { get; set; } = new();

    public LinuxPathsOptions Linux { get; set; } = new();

    public ListingOptions Listing { get; set; } = new();
}

public sealed class ImpersonationOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Dedicated impersonation threads. 0 = automatic.</summary>
    public int Threads { get; set; }

    /// <summary>Identity used by the startup self test (typically nobody/nogroup).</summary>
    public uint SelfTestUid { get; set; } = 65534;

    public uint SelfTestGid { get; set; } = 65534;
}

public sealed class AuthOptions
{
    /// <summary>"pam" or "shadow". PAM falls back to shadow automatically when libpam is unavailable.</summary>
    public string Provider { get; set; } = "shadow";

    public string PamService { get; set; } = "filemanager";

    public string CookieName { get; set; } = "fm_session";

    public int SessionIdleMinutes { get; set; } = 480;

    public int SessionAbsoluteHours { get; set; } = 168;

    public int MaxFailedAttempts { get; set; } = 10;

    public int LockoutMinutes { get; set; } = 15;

    public bool RequireSecureCookie { get; set; }

    /// <summary>Exposes the list of login capable host users to anonymous callers.</summary>
    public bool ExposeUserList { get; set; }

    /// <summary>Development only: accepts any user/password, replacing real host authentication.</summary>
    public bool AllowDevelopmentBypass { get; set; }

    public int MinPasswordLength { get; set; } = 8;

    /// <summary>Accounts below this uid are treated as system accounts.</summary>
    public uint MinimumUid { get; set; } = 1000;

    /// <summary>
    /// Window after a browser reports "page closed" during which the same session may come back
    /// (page reload). Afterwards the session counts as ended and no longer blocks a new sign-in.
    /// </summary>
    public int PageCloseGraceSeconds { get; set; } = 10;

    /// <summary>Includes system accounts in listings and in the login candidate set.</summary>
    public bool IncludeSystemUsers { get; set; }
}

public sealed class BootstrapOptions
{
    public bool Enabled { get; set; } = true;

    public string AdminUser { get; set; } = "a-admin";

    public string AdminPassword { get; set; } = "passwordDemand";

    public bool GrantSudo { get; set; } = true;

    public bool SudoNopasswd { get; set; }
}

public sealed class AclOptions
{
    public bool Enabled { get; set; } = true;

    public string SetFacl { get; set; } = "setfacl";

    public string GetFacl { get; set; } = "getfacl";

    /// <summary>Also writes default ACLs on directories so new children inherit access.</summary>
    public bool DefaultAclOnDirectories { get; set; } = true;
}

public sealed class UploadOptions
{
    public long MaxFileSizeBytes { get; set; } = 2048L * 1024 * 1024;

    public long MaxRequestSizeBytes { get; set; } = 4096L * 1024 * 1024;

    /// <summary>Directory used for staged uploads/downloads. Created on startup when needed.</summary>
    public string TempRoot { get; set; } = "/var/tmp/filemanager";
}

public sealed class LinuxPathsOptions
{
    /// <summary>How long parsed account files are cached. 0 disables caching.</summary>
    public int AccountCacheSeconds { get; set; } = 5;

    public string Passwd { get; set; } = "/etc/passwd";

    public string Shadow { get; set; } = "/etc/shadow";

    public string Group { get; set; } = "/etc/group";

    public string SudoersDir { get; set; } = "/etc/sudoers.d";

    public string UserAdd { get; set; } = "useradd";

    public string UserMod { get; set; } = "usermod";

    public string UserDel { get; set; } = "userdel";

    public string GroupAdd { get; set; } = "groupadd";

    public string GroupDel { get; set; } = "groupdel";

    public string ChPasswd { get; set; } = "chpasswd";

    public string Visudo { get; set; } = "visudo";

    public string Getent { get; set; } = "getent";
}

public sealed class ListingOptions
{
    public int MaxEntries { get; set; } = 5000;

    /// <summary>Hide uid/gid resolution failures instead of failing the listing.</summary>
    public bool ResolveOwners { get; set; } = true;
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>"postgres" or "sqlite".</summary>
    public string Provider { get; set; } = "postgres";

    public string ConnectionString { get; set; } = string.Empty;

    public bool MigrateOnStartup { get; set; } = true;

    public int ConnectRetrySeconds { get; set; } = 60;
}
