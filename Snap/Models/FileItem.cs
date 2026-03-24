using System.Windows.Media;

namespace Snap.Models;

public class FileItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public long Size { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public ImageSource? Icon { get; set; }
    public int FrequencyLevel { get; set; }

    public string DisplaySize => IsDirectory ? "" : FormatSize(Size);

    public string DisplayDate => LastModified.ToString("yyyy/MM/dd HH:mm");

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
