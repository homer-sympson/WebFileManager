using System.Runtime.InteropServices;

namespace FileManager.Core.Interop;

/// <summary>
/// Minimal libc surface used for credential switching and file metadata.
///
/// Credential changes deliberately use the <b>raw syscalls</b> instead of the glibc wrappers:
/// glibc implements the POSIX "all threads" semantics for the setxid family, so calling
/// <c>setresuid</c> from one thread also changes every other thread of the process - which would
/// break the promise that a request only ever runs with the credentials of the signed in user.
/// The kernel's own <c>setresuid</c> affects the calling thread only, which is exactly what the
/// dedicated impersonation workers rely on.
/// </summary>
internal static class Libc
{
    private const string Library = "libc";

    [DllImport(Library, SetLastError = true)]
    private static extern int setresuid(uint ruid, uint euid, uint suid);

    [DllImport(Library, SetLastError = true)]
    private static extern int setresgid(uint rgid, uint egid, uint sgid);

    [DllImport(Library, SetLastError = true)]
    private static extern int setgroups(nuint size, uint[] list);

    [DllImport(Library, SetLastError = true)]
    private static extern uint getuid();

    [DllImport(Library, SetLastError = true)]
    private static extern uint geteuid();

    [DllImport(Library, SetLastError = true)]
    private static extern uint getgid();

    [DllImport(Library, SetLastError = true)]
    private static extern uint getegid();

    [DllImport(Library, SetLastError = true, EntryPoint = "stat")]
    private static extern int stat(string path, IntPtr buffer);

    [DllImport(Library, SetLastError = true, EntryPoint = "syscall")]
    private static extern long Syscall(long number, uint a, uint b, uint c);

    [DllImport(Library, SetLastError = true, EntryPoint = "syscall")]
    private static extern long Syscall(long number, nuint a, IntPtr b);

    [DllImport(Library, SetLastError = true, EntryPoint = "syscall")]
    private static extern long Syscall(long number);

    /// <summary>Syscall numbers per architecture; <c>Supported</c> is false on untested targets.</summary>
    private static class Numbers
    {
        public static readonly bool Supported;
        public static readonly long SetResUid;
        public static readonly long SetResGid;
        public static readonly long SetGroups;
        public static readonly long GetEuid;
        public static readonly long GetEgid;

        static Numbers()
        {
            switch (RuntimeInformation.OSArchitecture)
            {
                case Architecture.X64:
                    SetResUid = 117;
                    SetResGid = 119;
                    SetGroups = 116;
                    GetEuid = 107;
                    GetEgid = 108;
                    Supported = true;
                    break;

                case Architecture.Arm64:
                    SetResUid = 147;
                    SetResGid = 149;
                    SetGroups = 159;
                    GetEuid = 175;
                    GetEgid = 177;
                    Supported = true;
                    break;

                default:
                    Supported = false;
                    break;
            }
        }
    }

    /// <summary>True when per-thread credential switching is available on this architecture.</summary>
    internal static bool SupportsPerThreadCredentials => Numbers.Supported;

    internal static int SetResUid(uint real, uint effective, uint saved) =>
        Numbers.Supported
            ? (int)Syscall(Numbers.SetResUid, real, effective, saved)
            : setresuid(real, effective, saved);

    internal static int SetResGid(uint real, uint effective, uint saved) =>
        Numbers.Supported
            ? (int)Syscall(Numbers.SetResGid, real, effective, saved)
            : setresgid(real, effective, saved);

    internal static unsafe int SetGroups(uint[] groups)
    {
        if (!Numbers.Supported)
        {
            return setgroups((nuint)groups.Length, groups);
        }

        fixed (uint* pointer = groups)
        {
            return (int)Syscall(Numbers.SetGroups, (nuint)groups.Length, (IntPtr)pointer);
        }
    }

    internal static uint GetEUid() => Numbers.Supported ? (uint)Syscall(Numbers.GetEuid) : geteuid();

    internal static uint GetEGid() => Numbers.Supported ? (uint)Syscall(Numbers.GetEgid) : getegid();

    internal static uint GetUid() => getuid();

    internal static uint GetGid() => getgid();

    internal static int Stat(string path, IntPtr buffer) => stat(path, buffer);

    /// <summary>
    /// x86-64 glibc <c>struct stat</c>. Other architectures report "unknown owner" instead of guessing.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Stat64
    {
        public ulong Dev;
        public ulong Ino;
        public ulong Nlink;
        public uint Mode;
        public uint Uid;
        public uint Gid;
        public int Pad0;
        public ulong Rdev;
        public long Size;
        public long Blksize;
        public long Blocks;
        public long AtimeSec;
        public long AtimeNsec;
        public long MtimeSec;
        public long MtimeNsec;
        public long CtimeSec;
        public long CtimeNsec;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }
}
