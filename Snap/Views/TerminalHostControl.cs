using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Snap.Views;

/// <summary>
/// pwsh コンソールウィンドウを WPF に埋め込むための HwndHost。
/// 内部に Win32 子ウィンドウを作成し、その中に外部プロセスのウィンドウを SetParent する。
/// </summary>
public class TerminalHostControl : HwndHost
{
    private IntPtr _hwndHost;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;

    /// <summary>このホストの Win32 ウィンドウハンドル</summary>
    public IntPtr HostWindowHandle => _hwndHost;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwndHost = CreateWindowEx(
            0, "static", "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, (int)Width, (int)Height,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        return new HandleRef(this, _hwndHost);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
    }
}
