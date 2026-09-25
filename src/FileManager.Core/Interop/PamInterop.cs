using System.Runtime.InteropServices;

namespace FileManager.Core.Interop;

/// <summary>libpam bindings: pam_start / pam_authenticate / pam_acct_mgmt / pam_end.</summary>
internal static class PamInterop
{
    private const string Library = "libpam.so.0";

    internal const int PamSuccess = 0;
    internal const int PamConversationError = 19;

    internal const int PromptEchoOff = 1;
    internal const int PromptEchoOn = 2;
    internal const int ErrorMessage = 3;
    internal const int TextInfo = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PamMessage
    {
        public int MsgStyle;
        public IntPtr Msg;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PamResponse
    {
        public IntPtr Resp;
        public int RespRetcode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PamConversation
    {
        public IntPtr Conv;
        public IntPtr AppDataPtr;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ConversationCallback(int numMsg, IntPtr msg, out IntPtr response, IntPtr appDataPtr);

    [DllImport(Library, EntryPoint = "pam_start", CharSet = CharSet.Ansi)]
    internal static extern int PamStart(string service, string user, ref PamConversation conversation, out IntPtr pamHandle);

    [DllImport(Library, EntryPoint = "pam_authenticate")]
    internal static extern int PamAuthenticate(IntPtr pamHandle, int flags);

    [DllImport(Library, EntryPoint = "pam_acct_mgmt")]
    internal static extern int PamAcctMgmt(IntPtr pamHandle, int flags);

    [DllImport(Library, EntryPoint = "pam_end")]
    internal static extern int PamEnd(IntPtr pamHandle, int status);

    [DllImport(Library, EntryPoint = "pam_strerror", CharSet = CharSet.Ansi)]
    internal static extern IntPtr PamStrError(IntPtr pamHandle, int errorNumber);
}
