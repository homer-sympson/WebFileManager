namespace FileManager.Core.Data;

/// <summary>
/// Raised when a client tries to sign in while the account already has a live session. Only an
/// explicit sign-out (or the end of the session) frees the account for another browser.
/// </summary>
public sealed class AlreadySignedInException : Exception
{
    public AlreadySignedInException(string userName, DateTime lastSeenUtc)
        : base($"Учётная запись {userName} уже используется в другом браузере.")
    {
        UserName = userName;
        LastSeenUtc = lastSeenUtc;
    }

    public string UserName { get; }

    public DateTime LastSeenUtc { get; }

    public const string Code = "already-signed-in";
}
