using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Snap.Models;
using Snap.Services;
using Snap.ViewModels;

namespace Snap.Views;

public partial class FolderTreeControl : UserControl
{
    public FolderTreeControl()
    {
        InitializeComponent();

        // A selection navigates the active pane only when it comes from the user's mouse or keys
        // (#23): the flag is set in the Preview phase of the input and cleared once the input event
        // has been through the tree (handled or not). Selections made by SyncToPathAsync, by a
        // re-created / recycled item container or by a layout pass happen outside that window and
        // only scroll the item into view.
        FolderTree.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), true);
        FolderTree.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => EndUserInput()), true);
        FolderTree.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        FolderTree.AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDownDone), true);
        FolderTree.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnPreviewMouseUp), true);
    }

    // True while a mouse click or a selection key is being handled by the tree.
    private bool _userInput;

    // The node that was already selected when the left button went down on it (#24), or null.
    // Clicking it again does not change the selection, so the Selected event does not fire;
    // the button-up on the same node navigates instead (e.g. back from a folder the tree
    // could not sync to, such as one under a hidden folder).
    private TreeNode? _reclickNode;

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _reclickNode = null;
        // A click on the expander arrow only expands / collapses (it does not select either).
        if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject) != null)
            return;
        _userInput = true;
        if (FindItem(e.OriginalSource as DependencyObject) is { IsSelected: true, DataContext: TreeNode node })
            _reclickNode = node;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var node = _reclickNode;
        _reclickNode = null;
        if (node == null || ViewModel == null) return;
        if (FindItem(e.OriginalSource as DependencyObject) is { IsSelected: true } tvi
            && ReferenceEquals(tvi.DataContext, node))
            ViewModel.OnNodeSelected(node);
    }

    /// <summary>The TreeViewItem that contains <paramref name="d"/> (the innermost one), or null.</summary>
    private static TreeViewItem? FindItem(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is TreeViewItem tvi) return tvi;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Up: case Key.Down: case Key.Left: case Key.Right:
            case Key.Home: case Key.End: case Key.PageUp: case Key.PageDown:
                _userInput = true;
                break;
            case Key.Enter: case Key.Space:
                // Enter / Space: open the selected folder even when the selection does not change.
                if (FolderTree.SelectedItem is TreeNode node && ViewModel != null)
                {
                    ViewModel.OnNodeSelected(node);
                    e.Handled = true;
                }
                break;
            default:
                // Typing a name selects the matching item (text search).
                if (key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9)
                    _userInput = true;
                break;
        }
    }

    private void OnKeyDownDone(object sender, KeyEventArgs e) => EndUserInput();

    private void EndUserInput() => _userInput = false;

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not TreeViewItem)
        {
            if (d is T t) return t;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private FolderTreeViewModel? ViewModel => DataContext as FolderTreeViewModel;

    private async void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        // async void event handler: nothing may escape to the dispatcher (#13).
        try { await TreeViewItem_ExpandedAsync(sender, e); }
        catch (Exception ex) { Log.UserError("FolderTree.Expand", "フォルダを展開できません", ex); }
    }

    private async Task TreeViewItem_ExpandedAsync(object sender, RoutedEventArgs e)
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
            // Auto-scroll to selected node
            tvi.BringIntoView();

            // Only a selection made by the user's click / keys moves the pane (#23).
            if (_userInput)
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
