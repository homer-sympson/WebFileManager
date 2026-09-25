using System.Runtime.InteropServices;

namespace FileManager.Core.Interop;

/// <summary>libcrypt bindings used by the shadow based fallback authenticator.</summary>
internal static class CryptInterop
{
    private const string Library = "libcrypt.so.1";

    /// <summary>Large enough for the SHA512/yescrypt state glibc keeps in struct crypt_data.</summary>
    internal const int CryptDataSize = 128 * 1024;

    [DllImport(Library, EntryPoint = "crypt_r", SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern IntPtr CryptR(string key, string salt, IntPtr data);
}
