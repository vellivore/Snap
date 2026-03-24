using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Snap.Models;

public partial class TreeNode : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _fullPath = "";

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<TreeNode> Children { get; } = new();

    // Dummy child for lazy loading (shows expand arrow before loading)
    public bool HasDummyChild => Children.Count == 1 && Children[0].FullPath == "__dummy__";

    public void AddDummyChild()
    {
        Children.Add(new TreeNode { FullPath = "__dummy__", Name = "読み込み中..." });
    }

    public void RemoveDummyChild()
    {
        if (HasDummyChild)
            Children.Clear();
    }
}
