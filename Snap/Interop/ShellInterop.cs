using System.Runtime.InteropServices;

namespace Snap.Interop;

// ==================== COM Interfaces ====================

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214E6-0000-0000-C000-000000000046")]
internal interface IShellFolder
{
    void ParseDisplayName(IntPtr hwnd, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
        out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

    void EnumObjects(IntPtr hwndOwner, uint grfFlags, out IntPtr ppenumIDList);

    void BindToObject(IntPtr pidl, IntPtr pbc,
        [In] ref Guid riid, out IntPtr ppv);

    void BindToStorage(IntPtr pidl, IntPtr pbc,
        [In] ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

    void CreateViewObject(IntPtr hwndOwner,
        [In] ref Guid riid, out IntPtr ppv);

    void GetAttributesOf(uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
        ref uint rgfInOut);

    void GetUIObjectOf(IntPtr hwndOwner, uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
        [In] ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

    void GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);

    void SetNameOf(IntPtr hwndOwner, IntPtr pidl,
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        uint uFlags, out IntPtr ppidlOut);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214E4-0000-0000-C000-000000000046")]
internal interface IContextMenu
{
    [PreserveSig]
    int QueryContextMenu(IntPtr hMenu, uint indexMenu,
        uint idCmdFirst, uint idCmdLast, uint uFlags);

    void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

    void GetCommandString(UIntPtr idCmd, uint uType,
        IntPtr pReserved, IntPtr pszName, uint cchMax);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214F4-0000-0000-C000-000000000046")]
internal interface IContextMenu2
{
    // IContextMenu methods
    [PreserveSig]
    int QueryContextMenu(IntPtr hMenu, uint indexMenu,
        uint idCmdFirst, uint idCmdLast, uint uFlags);
    void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    void GetCommandString(UIntPtr idCmd, uint uType,
        IntPtr pReserved, IntPtr pszName, uint cchMax);

    // IContextMenu2
    [PreserveSig]
    int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
internal interface IContextMenu3
{
    // IContextMenu methods
    [PreserveSig]
    int QueryContextMenu(IntPtr hMenu, uint indexMenu,
        uint idCmdFirst, uint idCmdLast, uint uFlags);
    void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    void GetCommandString(UIntPtr idCmd, uint uType,
        IntPtr pReserved, IntPtr pszName, uint cchMax);

    // IContextMenu2
    [PreserveSig]
    int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

    // IContextMenu3
    [PreserveSig]
    int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr result);
}

// ==================== Structures ====================

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CMINVOKECOMMANDINFOEX
{
    public int cbSize;
    public uint fMask;
    public IntPtr hwnd;
    public IntPtr lpVerb;
    [MarshalAs(UnmanagedType.LPStr)]
    public string? lpParameters;
    [MarshalAs(UnmanagedType.LPStr)]
    public string? lpDirectory;
    public int nShow;
    public uint dwHotKey;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.LPStr)]
    public string? lpTitle;
    public IntPtr lpVerbW;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? lpParametersW;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? lpDirectoryW;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? lpTitleW;
    public long ptInvoke; // POINT packed as long
}

// ==================== Native Methods ====================

internal static class ShellNativeMethods
{
    [DllImport("shell32.dll")]
    internal static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    internal static extern int SHBindToParent(
        IntPtr pidl, [In] ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenuEx(
        IntPtr hMenu, uint fuFlags, int x, int y,
        IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(
        IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr pv);

    // Constants
    internal const uint CMF_NORMAL = 0x00000000;
    internal const uint CMF_EXPLORE = 0x00000004;
    internal const uint CMF_CANRENAME = 0x00000010;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_NONOTIFY = 0x0080;
    internal const uint TPM_LEFTALIGN = 0x0000;
    internal const uint MF_SEPARATOR = 0x0800;
    internal const uint MF_STRING = 0x0000;
    internal const uint MF_BYPOSITION = 0x0400;

    internal const uint CMIC_MASK_UNICODE = 0x00004000;
    internal const int SW_SHOWNORMAL = 1;

    internal const uint WM_INITMENUPOPUP = 0x0117;
    internal const uint WM_DRAWITEM = 0x002B;
    internal const uint WM_MEASUREITEM = 0x002C;
    internal const uint WM_MENUCHAR = 0x0120;

    internal const uint FIRST_CMD_ID = 1;
    internal const uint LAST_CMD_ID = 30000;
    internal const uint SNAP_CMD_BASE = 30001;

    internal static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    internal static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
}
