using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Snap.Interop;

/// <summary>Opens the shell's Properties dialog for a file or folder (Alt+Enter, #14).</summary>
internal static class ShellProperties
{
    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    /// <summary>ShellExecuteEx(verb "properties", SEE_MASK_INVOKEIDLIST). The dialog is modeless
    /// and runs on the shell's own thread; this returns right away.</summary>
    public static bool Show(string path, out string? error)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
        };
        if (ShellExecuteEx(ref info))
        {
            error = null;
            return true;
        }
        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }
}
