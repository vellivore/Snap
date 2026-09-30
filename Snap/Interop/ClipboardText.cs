using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Snap.Interop;

/// <summary>
/// Puts plain text on the clipboard through Win32 (CF_UNICODETEXT), retrying while another
/// process holds the clipboard. WPF's Clipboard.SetDataObject(copy: true) fails in its flush
/// step with CLIPBRD_E_CANT_OPEN when clipboard watchers (history, security software) open the
/// clipboard right after a write, even though the text did arrive (#14, Ctrl+Shift+C).
/// </summary>
internal static class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <exception cref="Win32Exception">The clipboard stayed busy or the write failed.</exception>
    public static void Set(string text)
    {
        const int attempts = 10;
        int i = 0;
        while (!OpenClipboard(IntPtr.Zero))
        {
            if (++i >= attempts) throw new Win32Exception(Marshal.GetLastWin32Error());
            Thread.Sleep(50);
        }

        try
        {
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());

            var bytes = (text.Length + 1) * 2;
            var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var p = GlobalLock(hMem);
                if (p == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                    Marshal.WriteInt16(p, text.Length * 2, 0);
                }
                finally { GlobalUnlock(hMem); }

                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                hMem = IntPtr.Zero; // owned by the system now
            }
            finally
            {
                if (hMem != IntPtr.Zero) GlobalFree(hMem);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }
}
