using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Snap.Interop;


internal record SnapMenuItem(string Label, Action Handler);

internal static class ShellContextMenu
{
    /// <summary>
    /// Shows native shell context menu for one or more files/folders.
    /// </summary>
    public static void ShowContextMenu(IntPtr hwnd, string[] paths, int x, int y,
        Action? onRefresh = null, List<SnapMenuItem>? customItems = null,
        Action? onMenuReady = null)
    {
        if (paths.Length == 0) return;

        var pidls = new List<IntPtr>();
        IntPtr parentFolder = IntPtr.Zero;
        IShellFolder? shellFolder = null;
        IntPtr hMenu = IntPtr.Zero;
        IContextMenu? contextMenu = null;
        HwndSource? hwndSource = null;
        HwndSourceHook? hook = null;

        try
        {
            // Get parent folder - use first path's parent
            var parentPath = Path.GetDirectoryName(paths[0]);
            if (parentPath == null) return;

            // Get IShellFolder for parent
            shellFolder = GetShellFolder(parentPath);
            if (shellFolder == null) return;

            // Parse child PIDLs
            foreach (var path in paths)
            {
                var childName = Path.GetFileName(path);
                if (string.IsNullOrEmpty(childName)) childName = path; // root drives

                uint eaten = 0;
                uint attrs = 0;
                shellFolder.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, childName,
                    out eaten, out var childPidl, ref attrs);

                if (childPidl != IntPtr.Zero)
                    pidls.Add(childPidl);
            }

            if (pidls.Count == 0) return;

            // Get IContextMenu
            var pidlArray = pidls.ToArray();
            var iid = ShellNativeMethods.IID_IContextMenu;
            shellFolder.GetUIObjectOf(hwnd, (uint)pidlArray.Length, pidlArray,
                ref iid, IntPtr.Zero, out var contextMenuPtr);

            if (contextMenuPtr == IntPtr.Zero) return;

            contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
            Marshal.Release(contextMenuPtr);

            // Create and populate menu
            hMenu = ShellNativeMethods.CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;

            contextMenu.QueryContextMenu(hMenu,
                0, ShellNativeMethods.FIRST_CMD_ID, ShellNativeMethods.LAST_CMD_ID,
                ShellNativeMethods.CMF_EXPLORE | ShellNativeMethods.CMF_CANRENAME);

            // Add Snap custom items
            var snapCmdIds = AppendSnapItems(hMenu, customItems);

            // Notify caller that menu is ready (e.g. to restore cursor)
            onMenuReady?.Invoke();

            // Hook WndProc for IContextMenu2/3 and to suppress OLE drag-drop during menu
            var cm2 = contextMenu as IContextMenu2;
            var cm3 = contextMenu as IContextMenu3;
            hwndSource = HwndSource.FromHwnd(hwnd);
            hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                uint umsg = (uint)msg;

                // Suppress OLE drag-drop messages during context menu
                if (umsg == 0x0233 /* WM_DROPFILES */ ||
                    umsg == 0x0049 /* WM_COPYGLOBALDATA */)
                {
                    handled = true;
                    return IntPtr.Zero;
                }

                if (umsg == ShellNativeMethods.WM_MENUCHAR && cm3 != null)
                {
                    cm3.HandleMenuMsg2(umsg, wParam, lParam, out var result);
                    handled = true;
                    return result;
                }
                if ((umsg == ShellNativeMethods.WM_INITMENUPOPUP ||
                     umsg == ShellNativeMethods.WM_DRAWITEM ||
                     umsg == ShellNativeMethods.WM_MEASUREITEM) && cm2 != null)
                {
                    cm2.HandleMenuMsg(umsg, wParam, lParam);
                    handled = true;
                }
                return IntPtr.Zero;
            };
            hwndSource?.AddHook(hook);

            // Show menu and get selection
            int cmd = ShellNativeMethods.TrackPopupMenuEx(hMenu,
                ShellNativeMethods.TPM_RETURNCMD | ShellNativeMethods.TPM_NONOTIFY | ShellNativeMethods.TPM_LEFTALIGN,
                x, y, hwnd, IntPtr.Zero);

            // Remove hook before invoking command
            if (hook != null && hwndSource != null)
            {
                hwndSource.RemoveHook(hook);
                hook = null;
            }

            if (cmd > 0)
            {
                // Check if it's a Snap custom item
                if (snapCmdIds.TryGetValue((uint)cmd, out var snapAction))
                {
                    snapAction.Invoke();
                }
                else if (cmd >= ShellNativeMethods.FIRST_CMD_ID && cmd <= ShellNativeMethods.LAST_CMD_ID)
                {
                    // Shell command
                    InvokeShellCommand(contextMenu, cmd, parentPath, hwnd, x, y);
                    onRefresh?.Invoke();
                }
            }
        }
        catch (COMException) { }
        catch (Exception) { }
        finally
        {
            if (hook != null && hwndSource != null)
                hwndSource.RemoveHook(hook);

            if (hMenu != IntPtr.Zero)
                ShellNativeMethods.DestroyMenu(hMenu);

            foreach (var pidl in pidls)
                ShellNativeMethods.CoTaskMemFree(pidl);

            if (contextMenu != null)
                Marshal.FinalReleaseComObject(contextMenu);

            if (shellFolder != null)
                Marshal.FinalReleaseComObject(shellFolder);
        }
    }

    /// <summary>
    /// Shows native shell background context menu for a folder (right-click on empty space).
    /// </summary>
    public static void ShowBackgroundMenu(IntPtr hwnd, string folderPath, int x, int y,
        Action? onRefresh = null, List<SnapMenuItem>? customItems = null,
        Action? onMenuReady = null)
    {
        IShellFolder? shellFolder = null;
        IntPtr hMenu = IntPtr.Zero;
        IContextMenu? contextMenu = null;
        HwndSource? hwndSource = null;
        HwndSourceHook? hook = null;

        try
        {
            shellFolder = GetShellFolder(folderPath);
            if (shellFolder == null) return;

            // Get background context menu via CreateViewObject
            var iid = ShellNativeMethods.IID_IContextMenu;
            shellFolder.CreateViewObject(hwnd, ref iid, out var contextMenuPtr);

            if (contextMenuPtr == IntPtr.Zero) return;

            contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
            Marshal.Release(contextMenuPtr);

            hMenu = ShellNativeMethods.CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;

            contextMenu.QueryContextMenu(hMenu,
                0, ShellNativeMethods.FIRST_CMD_ID, ShellNativeMethods.LAST_CMD_ID,
                ShellNativeMethods.CMF_EXPLORE);

            var snapCmdIds = AppendSnapItems(hMenu, customItems);

            // Notify caller that menu is ready
            onMenuReady?.Invoke();

            // Hook WndProc for IContextMenu2/3 and to suppress OLE drag-drop during menu
            var cm2 = contextMenu as IContextMenu2;
            var cm3 = contextMenu as IContextMenu3;
            hwndSource = HwndSource.FromHwnd(hwnd);
            hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                uint umsg = (uint)msg;
                if (umsg == 0x0233 || umsg == 0x0049)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                if (umsg == ShellNativeMethods.WM_MENUCHAR && cm3 != null)
                {
                    cm3.HandleMenuMsg2(umsg, wParam, lParam, out var result);
                    handled = true;
                    return result;
                }
                if ((umsg == ShellNativeMethods.WM_INITMENUPOPUP ||
                     umsg == ShellNativeMethods.WM_DRAWITEM ||
                     umsg == ShellNativeMethods.WM_MEASUREITEM) && cm2 != null)
                {
                    cm2.HandleMenuMsg(umsg, wParam, lParam);
                    handled = true;
                }
                return IntPtr.Zero;
            };
            hwndSource?.AddHook(hook);

            int cmd = ShellNativeMethods.TrackPopupMenuEx(hMenu,
                ShellNativeMethods.TPM_RETURNCMD | ShellNativeMethods.TPM_NONOTIFY | ShellNativeMethods.TPM_LEFTALIGN,
                x, y, hwnd, IntPtr.Zero);

            if (hook != null && hwndSource != null)
            {
                hwndSource.RemoveHook(hook);
                hook = null;
            }

            if (cmd > 0)
            {
                if (snapCmdIds.TryGetValue((uint)cmd, out var snapAction))
                {
                    snapAction.Invoke();
                }
                else if (cmd >= ShellNativeMethods.FIRST_CMD_ID && cmd <= ShellNativeMethods.LAST_CMD_ID)
                {
                    InvokeShellCommand(contextMenu, cmd, folderPath, hwnd, x, y);
                    onRefresh?.Invoke();
                }
            }
        }
        catch (COMException) { }
        catch (Exception) { }
        finally
        {
            if (hook != null && hwndSource != null)
                hwndSource.RemoveHook(hook);

            if (hMenu != IntPtr.Zero)
                ShellNativeMethods.DestroyMenu(hMenu);

            if (contextMenu != null)
                Marshal.FinalReleaseComObject(contextMenu);

            if (shellFolder != null)
                Marshal.FinalReleaseComObject(shellFolder);
        }
    }

    // ==================== Helpers ====================

    private static IShellFolder? GetShellFolder(string path)
    {
        int hr = ShellNativeMethods.SHParseDisplayName(path, IntPtr.Zero,
            out var pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero) return null;

        try
        {
            var iid = ShellNativeMethods.IID_IShellFolder;
            hr = ShellNativeMethods.SHBindToParent(pidl, ref iid,
                out var parentPtr, out var childPidl);

            if (hr != 0 || parentPtr == IntPtr.Zero) return null;

            var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
            Marshal.Release(parentPtr);

            // Bind to the folder itself
            parent.BindToObject(childPidl, IntPtr.Zero, ref iid, out var folderPtr);
            Marshal.FinalReleaseComObject(parent);

            if (folderPtr == IntPtr.Zero) return null;

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            Marshal.Release(folderPtr);
            return folder;
        }
        finally
        {
            ShellNativeMethods.CoTaskMemFree(pidl);
        }
    }

    private static Dictionary<uint, Action> AppendSnapItems(IntPtr hMenu, List<SnapMenuItem>? customItems)
    {
        var cmdMap = new Dictionary<uint, Action>();
        if (customItems == null || customItems.Count == 0) return cmdMap;

        ShellNativeMethods.AppendMenu(hMenu, ShellNativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);

        uint cmdId = ShellNativeMethods.SNAP_CMD_BASE;
        foreach (var item in customItems)
        {
            ShellNativeMethods.AppendMenu(hMenu, ShellNativeMethods.MF_STRING,
                (UIntPtr)cmdId, item.Label);
            cmdMap[cmdId] = item.Handler;
            cmdId++;
        }

        return cmdMap;
    }

    private static void InvokeShellCommand(IContextMenu contextMenu, int cmd,
        string directory, IntPtr hwnd, int x, int y)
    {
        var offset = cmd - (int)ShellNativeMethods.FIRST_CMD_ID;
        var ci = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = ShellNativeMethods.CMIC_MASK_UNICODE,
            hwnd = hwnd,
            lpVerb = (IntPtr)offset,
            lpVerbW = (IntPtr)offset,
            lpDirectory = directory,
            lpDirectoryW = directory,
            nShow = ShellNativeMethods.SW_SHOWNORMAL,
        };

        try
        {
            contextMenu.InvokeCommand(ref ci);
        }
        catch (COMException) { }
    }
}
