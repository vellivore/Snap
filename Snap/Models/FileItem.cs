using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Snap.Models;

/// <summary>
/// One entry of the file list. Observable (#16) so the icon filled in later by the icon worker,
/// the frequency bar after opening a file and a size / date updated by the folder watcher show
/// up without reloading the list.
/// </summary>
public partial class FileItem : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }

    /// <summary>Has the Hidden attribute: listed only while hidden files are shown (Ctrl+H, #17).</summary>
    public bool IsHidden { get; set; }

    /// <summary>A drive of the PC view: the name cell shows its free space (#17).</summary>
    public bool IsDrive { get; set; }

    /// <summary>PC view: "空き 120.3 GB / 476.3 GB", or "—" when the drive is not ready.</summary>
    public string DriveSpaceText { get; set; } = string.Empty;

    /// <summary>PC view: used space in percent (0-100); null when unknown (no bar).</summary>
    public double? DriveUsedPercent { get; set; }

    public bool HasDriveUsage => DriveUsedPercent.HasValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayDate))]
    private DateTime _lastModified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySize))]
    private long _size;

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    private int _frequencyLevel;

    public string DisplaySize => IsDirectory && Size == 0 ? "" : FormatSize(Size);

    public string DisplayDate => LastModified == DateTime.MinValue ? "" : LastModified.ToString("yyyy/MM/dd HH:mm");

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
