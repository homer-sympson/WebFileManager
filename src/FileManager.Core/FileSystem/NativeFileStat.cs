using System.Runtime.InteropServices;
using FileManager.Core.Interop;

namespace FileManager.Core.FileSystem;

/// <summary>Reads the numeric owner of a path. Degrades to "unknown" on unsupported architectures.</summary>
public static class NativeFileStat
{
    public static bool TryGetOwner(string path, out uint uid, out uint gid)
    {
        uid = 0;
        gid = 0;

        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            return false;
        }

        var size = Marshal.SizeOf<Libc.Stat64>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (Libc.Stat(path, buffer) != 0)
            {
                return false;
            }

            var stat = Marshal.PtrToStructure<Libc.Stat64>(buffer);
            uid = stat.Uid;
            gid = stat.Gid;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
