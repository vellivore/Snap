using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace Snap.Interop;


internal record SnapMenuItem(string Label, Action Handler);

internal static class ShellContextMenu
{
    /// <summary>Timeout for forwarding owner-draw / submenu messages to the worker.</summary>
    private static readonly TimeSpan MenuMsgTimeout = TimeSpan.FromSeconds(3);

    private static int _showCount;

    /// <summary>
    /// Menu state built on <see cref="ShellMenuWorker"/>. The COM members must only be
    /// touched on the worker thread; the HMENU and PIDLs are thread-agnostic.
    /// </summary>
    private sealed class PreparedMenu
    {
        public IShellFolder? ShellFolder;
        public IContextMenu? ContextMenu;
        public IContextMenu2? ContextMenu2;
        public IContextMenu3? ContextMenu3;
        public IntPtr HMenu;
        public readonly List<IntPtr> Pidls = new();
        public string Directory = "";
        public Dictionary<uint, Action> SnapCmdIds = new();
    }

    /// <summary>
    /// Shows native shell context menu for one or more files/folders.
    /// Shell COM work runs on the worker STA; the popup itself is tracked on the UI thread.
    /// </summary>
    public static Task ShowContextMenuAsync(IntPtr hwnd, string[] paths, int x, int y,
        Action? onRefresh = null, List<SnapMenuItem>? customItems = null,
        Action? onMenuReady = null)
    {
        if (paths.Length == 0) return Task.CompletedTask;
        return ShowCoreAsync("item", hwnd, x, y, onRefresh, customItems, onMenuReady,
            () => PrepareItemMenu(hwnd, paths,
                ShellNativeMethods.CMF_EXPLORE | ShellNativeMethods.CMF_CANRENAME));
    }

    /// <summary>
    /// Shows native shell background context menu for a folder (right-click on empty space).
    /// </summary>
    public static Task ShowBackgroundMenuAsync(IntPtr hwnd, string folderPath, int x, int y,
        Action? onRefresh = null, List<SnapMenuItem>? customItems = null,
        Action? onMenuReady = null)
    {
        return ShowCoreAsync("background", hwnd, x, y, onRefresh, customItems, onMenuReady,
            () => PrepareBackgroundMenu(hwnd, folderPath, ShellNativeMethods.CMF_EXPLORE));
    }

    // ==================== Warm-up (called on the worker) ====================

    internal static void WarmUpItemMenu(string path)
    {
        var pm = PrepareItemMenu(IntPtr.Zero, new[] { path },
            ShellNativeMethods.CMF_EXPLORE | ShellNativeMethods.CMF_CANRENAME);
        if (pm != null) ReleaseOnWorker(pm);
    }

    internal static void WarmUpBackgroundMenu(string folderPath)
    {
        var pm = PrepareBackgroundMenu(IntPtr.Zero, folderPath, ShellNativeMethods.CMF_EXPLORE);
        if (pm != null) ReleaseOnWorker(pm);
    }

    internal static void TimingLog(string message)
    {
        var line = $"[Snap.ShellMenu] {DateTime.Now:HH:mm:ss.fff} {message}";
        System.Diagnostics.Debug.WriteLine(line);
        System.Diagnostics.Trace.WriteLine(line);
        try
        {
            var logPath = Environment.GetEnvironmentVariable("SNAP_MENU_TIMING_LOG");
            if (!string.IsNullOrEmpty(logPath))
                File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch { }
    }

    // ==================== Core ====================

    private static async Task ShowCoreAsync(string kind, IntPtr hwnd, int x, int y,
        Action? onRefresh, List<SnapMenuItem>? customItems, Action? onMenuReady,
        Func<PreparedMenu?> prepare)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int showIndex = Interlocked.Increment(ref _showCount);
        var worker = ShellMenuWorker.Instance;

        // ---- Phase A (worker): build IContextMenu + HMENU + Snap items ----
        PreparedMenu? pm;
        try
        {
            pm = await worker.InvokeAsync(() =>
            {
                var p = prepare();
                if (p != null)
                {
                    try { p.SnapCmdIds = AppendSnapItems(p.HMenu, customItems); }
                    catch { }
                }
                return p;
            });
        }
        catch
        {
            return;
        }
        if (pm == null) return;

        HwndSource? hwndSource = null;
        HwndSourceHook? hook = null;
        try
        {
            // ---- Phase B (UI): track the popup, owned by the pane's window ----
            onMenuReady?.Invoke();
            TimingLog($"{kind} menu #{showIndex}: ready in {sw.ElapsedMilliseconds} ms");

            var cm2 = pm.ContextMenu2;
            var cm3 = pm.ContextMenu3;
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

                // IContextMenu2/3 messages are forwarded to the worker (UI -> worker only).
                if (umsg == ShellNativeMethods.WM_MENUCHAR && cm3 != null)
                {
                    if (worker.TryInvoke(() =>
                        {
                            cm3.HandleMenuMsg2(umsg, wParam, lParam, out var r);
                            return r;
                        }, MenuMsgTimeout, out var result))
                    {
                        handled = true;
                        return result;
                    }
                    return IntPtr.Zero;
                }
                if ((umsg == ShellNativeMethods.WM_INITMENUPOPUP ||
                     umsg == ShellNativeMethods.WM_DRAWITEM ||
                     umsg == ShellNativeMethods.WM_MEASUREITEM) && cm2 != null)
                {
                    if (worker.TryInvoke(() => cm2.HandleMenuMsg(umsg, wParam, lParam),
                            MenuMsgTimeout, out _))
                        handled = true;
                }
                return IntPtr.Zero;
            };
            hwndSource?.AddHook(hook);

            // No TPM_NONOTIFY: it suppresses WM_INITMENUPOPUP, which IContextMenu2 needs to
            // populate dynamic submenus such as "New". TPM_RETURNCMD still prevents WM_COMMAND.
            int cmd = ShellNativeMethods.TrackPopupMenuEx(pm.HMenu,
                ShellNativeMethods.TPM_RETURNCMD | ShellNativeMethods.TPM_LEFTALIGN,
                x, y, hwnd, IntPtr.Zero);

            // Remove hook before invoking command
            if (hook != null && hwndSource != null)
            {
                hwndSource.RemoveHook(hook);
                hook = null;
            }

            // ---- Phase C: execute the selection ----
            if (cmd > 0)
            {
                if (pm.SnapCmdIds.TryGetValue((uint)cmd, out var snapAction))
                {
                    snapAction.Invoke(); // Snap item: UI thread
                }
                else if (cmd >= ShellNativeMethods.FIRST_CMD_ID && cmd <= ShellNativeMethods.LAST_CMD_ID)
                {
                    // Shell item: InvokeCommand on the worker, then refresh on the UI.
                    var cm = pm.ContextMenu!;
                    var dir = pm.Directory;
                    await worker.InvokeAsync(() => InvokeShellCommand(cm, cmd, dir, hwnd, x, y));
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

            // DestroyMenu / PIDL free are thread-agnostic; COM release must happen on the worker.
            var toRelease = pm;
            try
            {
                if (worker.Dispatcher.HasShutdownStarted)
                    ReleaseHandlesOnly(toRelease);
                else
                    _ = worker.Dispatcher.BeginInvoke(() => ReleaseOnWorker(toRelease));
            }
            catch
            {
                ReleaseHandlesOnly(toRelease);
            }
        }
    }

    // ==================== Worker-side preparation ====================

    private static PreparedMenu? PrepareItemMenu(IntPtr hwnd, string[] paths, uint flags)
    {
        var pm = new PreparedMenu();
        try
        {
            // Get parent folder - use first path's parent
            var parentPath = Path.GetDirectoryName(paths[0]);
            if (parentPath == null) { ReleaseOnWorker(pm); return null; }
            pm.Directory = parentPath;

            pm.ShellFolder = GetShellFolder(parentPath);
            if (pm.ShellFolder == null) { ReleaseOnWorker(pm); return null; }

            // Parse child PIDLs
            foreach (var path in paths)
            {
                var childName = Path.GetFileName(path);
                if (string.IsNullOrEmpty(childName)) childName = path; // root drives

                uint eaten = 0;
                uint attrs = 0;
                pm.ShellFolder.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, childName,
                    out eaten, out var childPidl, ref attrs);

                if (childPidl != IntPtr.Zero)
                    pm.Pidls.Add(childPidl);
            }
            if (pm.Pidls.Count == 0) { ReleaseOnWorker(pm); return null; }

            var pidlArray = pm.Pidls.ToArray();
            var iid = ShellNativeMethods.IID_IContextMenu;
            pm.ShellFolder.GetUIObjectOf(hwnd, (uint)pidlArray.Length, pidlArray,
                ref iid, IntPtr.Zero, out var contextMenuPtr);
            if (contextMenuPtr == IntPtr.Zero) { ReleaseOnWorker(pm); return null; }

            pm.ContextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
            Marshal.Release(contextMenuPtr);

            return FinishPrepare(pm, flags);
        }
        catch
        {
            ReleaseOnWorker(pm);
            return null;
        }
    }

    private static PreparedMenu? PrepareBackgroundMenu(IntPtr hwnd, string folderPath, uint flags)
    {
        var pm = new PreparedMenu { Directory = folderPath };
        try
        {
            pm.ShellFolder = GetShellFolder(folderPath);
            if (pm.ShellFolder == null) { ReleaseOnWorker(pm); return null; }

            // Get background context menu via CreateViewObject
            var iid = ShellNativeMethods.IID_IContextMenu;
            pm.ShellFolder.CreateViewObject(hwnd, ref iid, out var contextMenuPtr);
            if (contextMenuPtr == IntPtr.Zero) { ReleaseOnWorker(pm); return null; }

            pm.ContextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
            Marshal.Release(contextMenuPtr);

            return FinishPrepare(pm, flags);
        }
        catch
        {
            ReleaseOnWorker(pm);
            return null;
        }
    }

    private static PreparedMenu? FinishPrepare(PreparedMenu pm, uint flags)
    {
        pm.HMenu = ShellNativeMethods.CreatePopupMenu();
        if (pm.HMenu == IntPtr.Zero) { ReleaseOnWorker(pm); return null; }

        pm.ContextMenu!.QueryContextMenu(pm.HMenu,
            0, ShellNativeMethods.FIRST_CMD_ID, ShellNativeMethods.LAST_CMD_ID, flags);

        // QueryInterface on the worker so the RCWs stay bound to this apartment.
        pm.ContextMenu2 = pm.ContextMenu as IContextMenu2;
        pm.ContextMenu3 = pm.ContextMenu as IContextMenu3;
        return pm;
    }

    /// <summary>Full cleanup. Must run on the worker (releases COM objects).</summary>
    private static void ReleaseOnWorker(PreparedMenu pm)
    {
        ReleaseHandlesOnly(pm);
        try
        {
            if (pm.ContextMenu != null)
                Marshal.FinalReleaseComObject(pm.ContextMenu);
        }
        catch { }
        pm.ContextMenu = null;
        pm.ContextMenu2 = null;
        pm.ContextMenu3 = null;
        try
        {
            if (pm.ShellFolder != null)
                Marshal.FinalReleaseComObject(pm.ShellFolder);
        }
        catch { }
        pm.ShellFolder = null;
    }

    /// <summary>Destroys the HMENU and frees PIDLs (safe on any thread, idempotent).</summary>
    private static void ReleaseHandlesOnly(PreparedMenu pm)
    {
        if (pm.HMenu != IntPtr.Zero)
        {
            ShellNativeMethods.DestroyMenu(pm.HMenu);
            pm.HMenu = IntPtr.Zero;
        }
        foreach (var pidl in pm.Pidls)
            ShellNativeMethods.CoTaskMemFree(pidl);
        pm.Pidls.Clear();
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
