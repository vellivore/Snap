using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Snap.Interop;

/// <summary>
/// Windows Shell の IFileOperation を使ってコピー/移動/削除/新規フォルダー/名前変更を行うラッパー。
/// 管理者権限が必要な宛先（C:\ 直下等）では UAC 昇格プロンプトを自動表示し、
/// 進捗 UI・名前衝突ダイアログ・ごみ箱（元に戻す）にも対応する（エクスプローラ同等）。
/// </summary>
public static class ShellFileOperation
{
    public readonly record struct Result(bool Success, bool Aborted, string? Error);

    public static Task<Result> CopyAsync(IReadOnlyList<string> sources, string destDir)
        => RunAsync("Copy", UndoableFlags, fo => QueueEach(sources, destDir, (item, src, dest) =>
            // 同じフォルダへのコピーはシェルが黙って中断するため、重複しない名前を渡す
            fo.CopyItem(item, dest!, SameFolderCopyName(src, destDir), IntPtr.Zero)));

    public static Task<Result> MoveAsync(IReadOnlyList<string> sources, string destDir)
        => RunAsync("Move", UndoableFlags, fo => QueueEach(sources, destDir, (item, _, dest) =>
            fo.MoveItem(item, dest!, null, IntPtr.Zero)));

    /// <summary>
    /// 削除。既定はごみ箱へ（元に戻せる）。Snap 側で確認済みなのでシェルの確認は出さない（二重確認を避ける）。
    /// <paramref name="permanent"/>（Shift+Delete）は FOF_ALLOWUNDO を外して完全に削除し、
    /// 取り返しがつかないので FOF_NOCONFIRMATION も外してシェルの確認に任せる。
    /// </summary>
    public static Task<Result> DeleteAsync(IReadOnlyList<string> sources, bool permanent = false)
        => RunAsync(permanent ? "DeletePermanently" : "Delete",
            permanent ? FOF_NOCONFIRMMKDIR : UndoableFlags | FOF_NOCONFIRMATION,
            fo => QueueEach(sources, null, (item, _, _) => fo.DeleteItem(item, IntPtr.Zero)));

    /// <summary>
    /// <paramref name="dir"/> にフォルダー <paramref name="name"/> を作る（IFileOperation.NewItem・元に戻せる）。
    /// 名前の重複は呼び出し側で避けておくこと。
    /// </summary>
    public static Task<Result> NewFolderAsync(string dir, string name)
        => RunAsync("NewFolder", UndoableFlags, fo =>
        {
            var folder = CreateItem(dir);
            try { return fo.NewItem(folder, FILE_ATTRIBUTE_DIRECTORY, name, null, IntPtr.Zero) >= 0 ? 1 : 0; }
            finally { Marshal.ReleaseComObject(folder); }
        });

    /// <summary><paramref name="path"/> の名前を <paramref name="newName"/> に変える（IFileOperation.RenameItem・UAC と元に戻すに対応）。</summary>
    public static Task<Result> RenameAsync(string path, string newName)
        => RunAsync("Rename", UndoableFlags, fo =>
        {
            var item = CreateItem(path);
            try { return fo.RenameItem(item, newName, IntPtr.Zero) >= 0 ? 1 : 0; }
            finally { Marshal.ReleaseComObject(item); }
        });

    // Copy/Move の上書き確認・名前衝突はシェルに任せる（FOF_NOCONFIRMATION を付けない）。
    private const uint UndoableFlags = FOFX_ADDUNDORECORD | FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR;

    /// <param name="queue">操作を積み、積めた数を返す（0 なら「対象が見つかりません」）。</param>
    private static Task<Result> RunAsync(string name, uint flags, Func<IFileOperation, int> queue)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        IntPtr owner;
        try { owner = ResolveOwner(); }
        catch (Exception ex)
        {
            Snap.Services.Log.Warn("ShellFileOperation.Owner", "owner window not resolved; dialogs unowned", ex);
            owner = IntPtr.Zero;
        }

        // IFileOperation は STA かつ独自に UI（UAC/進捗/衝突ダイアログ）を出すため、
        // UI スレッドをブロックしないよう専用 STA スレッドで実行する。
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(Execute(flags, queue, owner)); }
            catch (Exception ex)
            {
                Snap.Services.Log.Error("ShellFileOperation", $"{name} failed", ex);
                tcs.TrySetResult(new Result(false, false, ex.Message));
            }
        })
        { IsBackground = true, Name = "ShellFileOperation" };
        thread.SetApartmentState(ApartmentState.STA);
        try { thread.Start(); }
        catch (Exception ex)
        {
            Snap.Services.Log.Error("ShellFileOperation", "worker thread not started", ex);
            tcs.TrySetResult(new Result(false, false, ex.Message));
        }
        return tcs.Task;
    }

    private static IntPtr ResolveOwner()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return IntPtr.Zero;
        return disp.Invoke(() =>
        {
            // アクティブなウィンドウ（なければ MainWindow）を親にする
            var w = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive)
                    ?? Application.Current?.MainWindow;
            return w != null ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        });
    }

    private static Result Execute(uint flags, Func<IFileOperation, int> queue, IntPtr owner)
    {
        var type = Type.GetTypeFromCLSID(CLSID_FileOperation)
            ?? throw new InvalidOperationException("IFileOperation は利用できません");
        var fo = (IFileOperation)Activator.CreateInstance(type)!;
        try
        {
            if (owner != IntPtr.Zero)
                fo.SetOwnerWindow(owner);
            ThrowIfFailed(fo.SetOperationFlags(flags));

            if (queue(fo) == 0)
                return new Result(false, false, "対象が見つかりません");

            int hr = fo.PerformOperations();
            fo.GetAnyOperationsAborted(out bool aborted);

            if (hr == COPYENGINE_E_USER_CANCELLED)
                return new Result(false, true, DescribeHResult(hr));
            if (hr < 0)
                return new Result(false, aborted, DescribeHResult(hr));
            return new Result(!aborted, aborted, null);
        }
        finally
        {
            Marshal.ReleaseComObject(fo);
        }
    }

    /// <summary>Queues one operation per source (unresolvable sources are skipped) and returns how many were queued.</summary>
    private static int QueueEach(IReadOnlyList<string> sources, string? destDir,
        Func<IShellItem, string, IShellItem?, int> queueOne)
    {
        IShellItem? dest = null;
        try
        {
            if (destDir != null)
                dest = CreateItem(destDir);

            int queued = 0;
            foreach (var src in sources)
            {
                IShellItem item;
                try { item = CreateItem(src); }
                catch (Exception ex)
                {
                    // 解決できないソースはスキップ
                    Snap.Services.Log.Warn("ShellFileOperation", $"source skipped (unresolvable): {src}", ex);
                    continue;
                }
                try
                {
                    if (queueOne(item, src, dest) >= 0) queued++;
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            return queued;
        }
        finally
        {
            if (dest != null) Marshal.ReleaseComObject(dest);
        }
    }

    /// <summary>
    /// コピー元と同じフォルダへのコピーなら「名前 (2).ext」形式の空き名を返す。それ以外は null（元の名前のまま）。
    /// </summary>
    private static string? SameFolderCopyName(string src, string destDir)
    {
        var trimmed = src.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var parent = System.IO.Path.GetDirectoryName(trimmed);
        if (parent == null || !Snap.Services.FileSystemService.SamePath(parent, destDir))
            return null;

        var name = System.IO.Path.GetFileName(trimmed);
        bool isDir = System.IO.Directory.Exists(trimmed);
        return Snap.Services.FileSystemService.UniqueName(destDir, name, isDir, keepOriginal: false);
    }

    private static string DescribeHResult(int hr) => hr switch
    {
        E_ACCESSDENIED => "アクセスが拒否されました（権限がありません）",
        COPYENGINE_E_USER_CANCELLED => "操作が取り消されました",
        _ => $"HRESULT 0x{hr:X8}",
    };

    private static void ThrowIfFailed(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static IShellItem CreateItem(string path)
    {
        var iid = IID_IShellItem;
        return SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid);
    }

    // ==================== COM interop ====================

    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x0010;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    private const uint FOFX_ADDUNDORECORD = 0x20000000;

    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int COPYENGINE_E_USER_CANCELLED = unchecked((int)0x80270000);

    private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern IShellItem SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid);

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr pfops, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        [PreserveSig] int SetProgressDialog(IntPtr popd);
        [PreserveSig] int SetProperties(IntPtr pproparray);
        [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem psiItem);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr punkItems);
        [PreserveSig] int RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        [PreserveSig] int RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);
        [PreserveSig] int MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        [PreserveSig] int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);
        [PreserveSig] int CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        [PreserveSig] int DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
        [PreserveSig] int DeleteItems(IntPtr punkItems);
        [PreserveSig] int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
