using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Snap.Models;
using Snap.ViewModels;

namespace Snap.Views;

public partial class FolderTreeControl : UserControl
{
    public FolderTreeControl()
    {
        InitializeComponent();
    }

    private FolderTreeViewModel? ViewModel => DataContext as FolderTreeViewModel;

    private async void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is TreeNode node && ViewModel != null)
        {
            await ViewModel.ExpandNodeAsync(node);
        }
    }

    private void TreeViewItem_Selected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem tvi && tvi.DataContext is TreeNode node && ViewModel != null)
        {
            // プログラムからの同期中（SyncToPathAsync）はペインへのナビゲーションを発火しない
            if (!ViewModel.IsSyncing)
                ViewModel.OnNodeSelected(node);
            e.Handled = true;
        }
    }

    // --- ブックマーク ---

    private void Bookmark_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BookmarkItem item && ViewModel != null)
        {
            ViewModel.OnBookmarkSelected(item);
            e.Handled = true;
        }
    }

    private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
    {
        // ContextMenu は visual tree 外なので、PlacementTarget 経由で DataContext を取得
        BookmarkItem? item = null;
        if (sender is MenuItem mi)
        {
            if (mi.DataContext is BookmarkItem bm)
                item = bm;
            else if (mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe
                     && fe.DataContext is BookmarkItem bm2)
                item = bm2;
        }

        if (item != null && ViewModel != null)
        {
            ViewModel.RemoveBookmark(item);
        }
    }
}
