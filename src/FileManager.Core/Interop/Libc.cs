using System.Runtime.InteropServices;

namespace FileManager.Core.Interop;

/// <summary>Minimal libc surface used for credential switching and file metadata.</summary>
internal static class Libc
{
    private const string Library = "libc";

    [DllImport(Library, SetLastError = true)]
    internal static extern int setresuid(uint ruid, uint euid, uint suid);

    [DllImport(Library, SetLastError = true)]
    internal static extern int setresgid(uint rgid, uint egid, uint sgid);

    [DllImport(Library, SetLastError = true)]
    internal static extern int setgroups(nuint size, uint[] list);

    [DllImport(Library, SetLastError = true)]
    internal static extern uint getuid();

    [DllImport(Library, SetLastError = true)]
    internal static extern uint geteuid();

    [DllImport(Library, SetLastError = true)]
    internal static extern uint getgid();

    [DllImport(Library, SetLastError = true)]
    internal static extern uint getegid();

    [DllImport(Library, SetLastError = true, EntryPoint = "stat")]
    internal static extern int stat(string path, IntPtr buffer);

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
