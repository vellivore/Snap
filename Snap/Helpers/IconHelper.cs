using System.Collections.Concurrent;
using System.Globalization;
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
    private const uint SHGFI_SYSICONINDEX = 0x4000;

    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // iIcon インデックスでキャッシュ（同じアイコンを使い回す）
    private static readonly ConcurrentDictionary<int, (ImageSource? icon, string typeName)> _folderIconCache = new();
    private static readonly ConcurrentDictionary<string, (ImageSource? icon, string typeName)> _fileIconCache = new();

    // ==================== 種別バッジ（汎用アイコンに潰れる拡張子の補完） ====================
    // アイコンハンドラ未登録の拡張子（.cs/.py/.yml 等）はシェルが汎用の白紙アイコンを返す。
    // それらだけを、カテゴリ色＋拡張子ラベルの自作バッジに差し替えて視覚的に区別する。

    private static int _genericFileIconIndex = int.MinValue;

    /// <summary>「アイコン未登録」のファイルが返す汎用アイコンの索引を実機から取得（キャッシュ）。</summary>
    private static int GetGenericFileIconIndex()
    {
        if (_genericFileIconIndex == int.MinValue)
        {
            var shfi = new SHFILEINFO();
            // 確実に未登録の拡張子で汎用アイコン索引を得る
            SHGetFileInfo("dummy.__snap_no_such_ext__", FILE_ATTRIBUTE_NORMAL, ref shfi,
                (uint)Marshal.SizeOf(shfi), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
            _genericFileIconIndex = shfi.iIcon;
        }
        return _genericFileIconIndex;
    }

    private static readonly Color CodeColor = Color.FromRgb(0x3B, 0x82, 0xF6);   // 青: ソースコード
    private static readonly Color ScriptColor = Color.FromRgb(0x22, 0xC5, 0x5E); // 緑: スクリプト
    private static readonly Color ConfigColor = Color.FromRgb(0xF5, 0x9E, 0x0B); // 橙: 設定/データ定義
    private static readonly Color DataColor = Color.FromRgb(0xF4, 0x3F, 0x5E);   // 赤: データ
    private static readonly Color WebColor = Color.FromRgb(0xA8, 0x55, 0xF7);    // 紫: Web
    private static readonly Color TextColor = Color.FromRgb(0x14, 0xB8, 0xA6);   // 青緑: 文書テキスト
    private static readonly Color DefaultColor = Color.FromRgb(0x6B, 0x72, 0x80); // 灰: その他

    private static readonly Dictionary<string, Color> _extColor = BuildExtColorMap();

    private static Dictionary<string, Color> BuildExtColorMap()
    {
        var map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        void Add(Color c, params string[] exts) { foreach (var e in exts) map["." + e] = c; }
        Add(CodeColor, "cs", "py", "js", "ts", "jsx", "tsx", "go", "rs", "rb", "php",
                       "java", "kt", "kts", "swift", "c", "cpp", "cc", "cxx", "h", "hpp",
                       "m", "mm", "dart", "scala", "lua", "r", "pl", "vb", "fs", "fsx", "clj", "ex", "exs");
        Add(ScriptColor, "sh", "bash", "zsh", "fish", "bat", "cmd", "ps1", "psm1", "psd1");
        Add(ConfigColor, "yml", "yaml", "toml", "ini", "cfg", "conf", "env", "properties", "editorconfig", "lock");
        Add(DataColor, "csv", "tsv", "sql", "log", "dat", "ndjson", "parquet");
        Add(WebColor, "vue", "svelte", "astro", "scss", "sass", "less", "styl");
        Add(TextColor, "rst", "tex", "adoc", "org", "markdown", "mdx");
        return map;
    }

    private static Color CategoryColorFor(string ext) =>
        _extColor.TryGetValue(ext, out var c) ? c : DefaultColor;

    /// <summary>カテゴリ色＋拡張子ラベルの 32px バッジ ImageSource を生成する（UIスレッドで実行）。</summary>
    private static ImageSource CreateBadgeIcon(string label, Color color)
    {
        const int px = 32;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new SolidColorBrush(color);
            dc.DrawRoundedRectangle(bg, null, new Rect(1, 1, px - 2, px - 2), 6, 6);

            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                FontWeights.Bold, FontStretches.Normal);
            double fontSize = label.Length <= 2 ? 14 : label.Length == 3 ? 11 : 8.5;
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.White, 1.0)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = px - 2,
            };
            dc.DrawText(ft, new Point(1, (px - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static T RunOnUi<T>(Func<T> f)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null || disp.CheckAccess())
            return f();
        return disp.Invoke(f);
    }

    /// <summary>シェルアイコンが汎用（白紙）に潰れる拡張子なら自作バッジに差し替える。</summary>
    private static (ImageSource? icon, string typeName) ApplyBadgeIfGeneric(
        string ext, (ImageSource? icon, string typeName) shell, int iIcon)
    {
        if (ext.Length > 1 && iIcon == GetGenericFileIconIndex())
        {
            var label = ext.TrimStart('.').ToUpperInvariant();
            if (label.Length > 4) label = label[..4];
            var color = CategoryColorFor(ext);
            var badge = RunOnUi(() => CreateBadgeIcon(label, color));
            return (badge, shell.typeName);
        }
        return shell;
    }

    public static (ImageSource? icon, string typeName) GetIconAndType(string path, bool isDirectory)
    {
        // Network paths: use generic icons (fast, no network access)
        if (path.StartsWith(@"\\"))
        {
            if (isDirectory)
            {
                return _fileIconCache.GetOrAdd("\\dir", _ =>
                    GetIconFromShell("folder", FILE_ATTRIBUTE_DIRECTORY, useFileAttributes: true));
            }
            var netExt = Path.GetExtension(path).ToLowerInvariant();
            if (string.IsNullOrEmpty(netExt)) netExt = ".";
            return _fileIconCache.GetOrAdd(netExt, _ =>
            {
                var shell = GetIconFromShell("dummy" + netExt, FILE_ATTRIBUTE_NORMAL, true, out int iIcon);
                return ApplyBadgeIfGeneric(netExt, shell, iIcon);
            });
        }

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
            var shell = GetIconFromShell(dummyPath, FILE_ATTRIBUTE_NORMAL, true, out int iIcon);
            return ApplyBadgeIfGeneric(ext, shell, iIcon);
        });
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
        => GetIconFromShell(path, attributes, useFileAttributes, out _);

    private static (ImageSource? icon, string typeName) GetIconFromShell(string path, uint attributes, bool useFileAttributes, out int sysIconIndex)
    {
        sysIconIndex = 0;
        var shfi = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_TYPENAME;
        if (useFileAttributes)
            flags |= SHGFI_USEFILEATTRIBUTES;

        var result = SHGetFileInfo(path, attributes, ref shfi, (uint)Marshal.SizeOf(shfi), flags);

        if (result == IntPtr.Zero)
            return (null, attributes == FILE_ATTRIBUTE_DIRECTORY ? "フォルダー" : "ファイル");

        sysIconIndex = shfi.iIcon;

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
