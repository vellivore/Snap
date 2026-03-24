using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Snap.Models;

public partial class BookmarkItem : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _fullPath = "";

    [ObservableProperty]
    private ImageSource? _icon;

    public string ParentPath => Path.GetDirectoryName(FullPath) ?? FullPath;
}
