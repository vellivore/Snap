using System.IO;
using System.Runtime.InteropServices;

namespace Snap.Services;

/// <summary>
/// File-system queries shared by the file list and the folder tree (#15): drives, network
/// shares (NetShareEnum), UNC classification, path comparison and free names.
/// </summary>
public static class FileSystemService
{
    public sealed record DriveEntry(string RootPath, string Label, string TypeName, long TotalSize);

    public sealed record ShareEntry(string Name, string FullPath, string Remark);

    /// <summary>
    /// Every drive, labelled "Volume (C:)" (or "C:" when not ready / unlabelled).
    /// Drives that throw while being read are skipped, logged and counted in <paramref name="skipped"/>.
    /// </summary>
    public static List<DriveEntry> GetDrives(out int skipped)
    {
        var list = new List<DriveEntry>();
        skipped = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var letter = drive.Name.TrimEnd('\\');
                var ready = drive.IsReady;
                var label = ready && !string.IsNullOrEmpty(drive.VolumeLabel)
                    ? $"{drive.VolumeLabel} ({letter})"
                    : letter;
                var typeName = drive.DriveType switch
                {
                    DriveType.Fixed => "ローカル ディスク",
                    DriveType.Removable => "リムーバブル ディスク",
                    DriveType.Network => "ネットワーク ドライブ",
                    DriveType.CDRom => "CD/DVD ドライブ",
                    DriveType.Ram => "RAM ディスク",
                    _ => "ドライブ",
                };
                list.Add(new DriveEntry(drive.Name, label, typeName, ready ? drive.TotalSize : 0));
            }
            catch (Exception ex)
            {
                skipped++;
                Log.Warn("FileSystem.Drives", drive.Name, ex);
            }
        }
        return list;
    }

    /// <summary>\\server (no share name).</summary>
    public static bool IsUncServerPath(string path)
    {
        if (!path.StartsWith(@"\\")) return false;
        var afterPrefix = path.TrimEnd('\\')[2..];
        return afterPrefix.Length > 0 && !afterPrefix.Contains('\\');
    }

    /// <summary>
    /// A UNC path or a path on a mapped network drive (#16: no folder watcher and no per-folder
    /// icons there). Only asks the drive type (GetDriveType), which does not touch the network.
    /// </summary>
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.StartsWith(@"\\")) return true;
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex)
        {
            Log.Warn("FileSystem.IsNetworkPath", path, ex);
            return false;
        }
    }

    /// <summary>The drive letter (upper case) of a local path such as C:\foo, or null (UNC, PC view).</summary>
    public static char? DriveLetterOf(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
            return null;
        return char.ToUpperInvariant(path[0]);
    }

    /// <summary>
    /// The visible disk shares of <paramref name="serverPath"/> (\\server). Hidden ($) and
    /// non-disk shares are left out. Throws <see cref="DirectoryNotFoundException"/> when the
    /// server cannot be enumerated.
    /// </summary>
    public static List<ShareEntry> GetShares(string serverPath)
    {
        var server = serverPath.TrimEnd('\\');
        int resumeHandle = 0;
        int result = NetShareEnum(server, 1, out var bufPtr, -1, out int entriesRead, out _, ref resumeHandle);
        if (result != 0 || bufPtr == IntPtr.Zero)
            throw new DirectoryNotFoundException($"ネットワーク共有を列挙できません: {server} (エラーコード: {result})");

        var list = new List<ShareEntry>();
        try
        {
            var structSize = Marshal.SizeOf<SHARE_INFO_1>();
            var ptr = bufPtr;
            for (int i = 0; i < entriesRead; i++)
            {
                var info = Marshal.PtrToStructure<SHARE_INFO_1>(ptr);
                ptr = IntPtr.Add(ptr, structSize);
                if (info.shi1_netname.EndsWith('$')) continue;
                if ((info.shi1_type & ~STYPE_SPECIAL) != STYPE_DISKTREE) continue;
                list.Add(new ShareEntry(info.shi1_netname, $"{server}\\{info.shi1_netname}", info.shi1_remark ?? ""));
            }
        }
        finally
        {
            NetApiBufferFree(bufPtr);
        }
        return list;
    }

    /// <summary>Same folder, ignoring case and trailing separators (C:\a\ = c:\A).</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (a == null || b == null) return false;
        return string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies below it
    /// (separator-aware: C:\FooBar is not under C:\Foo).</summary>
    public static bool IsSameOrUnder(string path, string root)
    {
        if (SamePath(path, root)) return true;
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folder holding <paramref name="path"/> (null for a drive root / \\server\share).</summary>
    public static string? ParentOf(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>
    /// A name not yet taken in <paramref name="dir"/>: <paramref name="name"/> itself when free and
    /// <paramref name="keepOriginal"/>, else "name (2).ext", "name (3).ext", ... (a folder keeps
    /// dots in its name). Null if nothing free was found.
    /// </summary>
    public static string? UniqueName(string dir, string name, bool isDirectory, bool keepOriginal = true)
    {
        bool Taken(string candidate)
        {
            var full = Path.Combine(dir, candidate);
            return File.Exists(full) || Directory.Exists(full);
        }

        if (keepOriginal && !Taken(name)) return name;
        var baseName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDirectory ? "" : Path.GetExtension(name);
        for (int n = 2; n < 10000; n++)
        {
            var candidate = $"{baseName} ({n}){ext}";
            if (!Taken(candidate)) return candidate;
        }
        return null;
    }

    // ==================== NetShareEnum ====================

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(
        string serverName, int level, out IntPtr bufPtr, int prefMaxLen,
        out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string shi1_netname;
        public uint shi1_type;
        [MarshalAs(UnmanagedType.LPWStr)] public string shi1_remark;
    }

    private const uint STYPE_DISKTREE = 0x00000000;
    private const uint STYPE_SPECIAL = 0x80000000;
}
