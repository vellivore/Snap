using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snap.Helpers;

public static class IconHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint SHGFI_TYPENAME = 0x400;

    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private const uint COINIT_APARTMENTTHREADED = 0x2;

    [ThreadStatic]
    private static bool _comInitialized;

    // iIcon インデックスでキャッシュ（同じアイコンを使い回す）
    private static readonly ConcurrentDictionary<int, (ImageSource? icon, string typeName)> _folderIconCache = new();
    private static readonly ConcurrentDictionary<string, (ImageSource? icon, string typeName)> _fileIconCache = new();

    public static (ImageSource? icon, string typeName) GetIconAndType(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return GetFolderIcon(path);
        }

        // ファイル: 拡張子でキャッシュ
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
            ext = ".";

        return _fileIconCache.GetOrAdd(ext, _ =>
        {
            var dummyPath = "dummy" + ext;
            return GetIconFromShell(dummyPath, FILE_ATTRIBUTE_NORMAL, useFileAttributes: true);
        });
    }

    private static void EnsureComInitialized()
    {
        if (!_comInitialized)
        {
            CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            _comInitialized = true;
        }
    }

    private static (ImageSource? icon, string typeName) GetFolderIcon(string path)
    {
        // SHGetFileInfo をUIスレッドで呼ぶ（バックグラウンドスレッドだと hIcon=0 になる場合がある）
        SHFILEINFO shfi = default;
        IntPtr result = IntPtr.Zero;
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_TYPENAME;

        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            shfi = new SHFILEINFO();
            result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
        }
        else
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                shfi = new SHFILEINFO();
                result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), flags);
            });
        }

        if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
        {
            return GetIconFromShell(path, FILE_ATTRIBUTE_DIRECTORY, useFileAttributes: true);
        }

        var iIcon = shfi.iIcon;

        if (_folderIconCache.TryGetValue(iIcon, out var cached))
        {
            DestroyIcon(shfi.hIcon);
            return cached;
        }

        ImageSource? icon = null;
        try
        {
            icon = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }

        string typeName = string.IsNullOrEmpty(shfi.szTypeName) ? "フォルダー" : shfi.szTypeName;
        var entry = (icon, typeName);
        _folderIconCache.TryAdd(iIcon, entry);
        return entry;
    }

    private static (ImageSource? icon, string typeName) GetIconFromShell(string path, uint attributes, bool useFileAttributes)
    {
        var shfi = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_TYPENAME;
        if (useFileAttributes)
            flags |= SHGFI_USEFILEATTRIBUTES;

        var result = SHGetFileInfo(path, attributes, ref shfi, (uint)Marshal.SizeOf(shfi), flags);

        if (result == IntPtr.Zero)
            return (null, attributes == FILE_ATTRIBUTE_DIRECTORY ? "フォルダー" : "ファイル");

        ImageSource? icon = null;
        try
        {
            if (shfi.hIcon != IntPtr.Zero)
            {
                icon = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
            }
        }
        finally
        {
            if (shfi.hIcon != IntPtr.Zero)
                DestroyIcon(shfi.hIcon);
        }

        string typeName = string.IsNullOrEmpty(shfi.szTypeName)
            ? (attributes == FILE_ATTRIBUTE_DIRECTORY ? "フォルダー" : "ファイル")
            : shfi.szTypeName;

        return (icon, typeName);
    }
}
