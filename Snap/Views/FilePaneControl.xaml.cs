using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Snap.Helpers;
using Snap.Interop;
using Snap.Models;
using Snap.ViewModels;
using System.Linq;

namespace Snap.Views;

public partial class FilePaneControl : UserControl
{
    /// <summary>True while native shell context menu is open (suppress drop events across all panes).</summary>
    private static bool _shellMenuOpen;
    // File drag & drop state
    private Point _fileDragStartPoint;
    private bool _fileDragInProgress;
    private const double FileDragThreshold = 8.0;
    private readonly DragGhostHelper _dragGhost = new();
    /// <summary>
    /// ブックマーク追加要求のルーティドイベント。パスは Tag に格納。
    /// </summary>
    public static readonly RoutedEvent AddBookmarkRequestedEvent =
        EventManager.RegisterRoutedEvent("AddBookmarkRequested", RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(FilePaneControl));

    public event RoutedEventHandler AddBookmarkRequested
    {
        add => AddHandler(AddBookmarkRequestedEvent, value);
        remove => RemoveHandler(AddBookmarkRequestedEvent, value);
    }

    public FilePaneControl()
    {
        InitializeComponent();
        MouseDown += FilePaneControl_MouseDown;
        DataContextChanged += OnDataContextChanged;
    }

    private FilePaneViewModel? ViewModel => DataContext as FilePaneViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FilePaneViewModel oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is FilePaneViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            RebuildBreadcrumb(newVm.CurrentPath);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePaneViewModel.CurrentPath) && sender is FilePaneViewModel vm)
        {
            RebuildBreadcrumb(vm.CurrentPath);
        }
    }

    private void RebuildBreadcrumb(string path)
    {
        BreadcrumbBar.Items.Clear();

        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var parts = new List<(string name, string fullPath)>();

            // UNC path
            if (fullPath.StartsWith(@"\\"))
            {
                var segments = fullPath.TrimStart('\\').Split('\\');
                var built = @"\\";
                for (int i = 0; i < segments.Length; i++)
                {
                    built = i == 0 ? @"\\" + segments[i] : Path.Combine(built, segments[i]);
                    parts.Add((segments[i], built));
                }
            }
            else
            {
                // Drive root
                var root = Path.GetPathRoot(fullPath);
                if (root != null)
                {
                    parts.Add((root.TrimEnd('\\'), root));
                    var remaining = fullPath.Substring(root.Length);
                    if (!string.IsNullOrEmpty(remaining))
                    {
                        var segments = remaining.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                        var built = root;
                        foreach (var seg in segments)
                        {
                            built = Path.Combine(built, seg);
                            parts.Add((seg, built));
                        }
                    }
                }
            }

            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0)
                {
                    // Separator
                    var sep = new TextBlock
                    {
                        Text = "›",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(2, 0, 2, 0),
                        FontSize = 12
                    };
                    BreadcrumbBar.Items.Add(sep);
                }

                // アイコン + テキストの StackPanel
                var btnContent = new StackPanel { Orientation = Orientation.Horizontal };
                var (segIcon, _) = IconHelper.GetIconAndType(parts[i].fullPath, true);
                if (segIcon != null)
                {
                    btnContent.Children.Add(new Image
                    {
                        Source = segIcon,
                        Width = 14,
                        Height = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 3, 0)
                    });
                }
                btnContent.Children.Add(new TextBlock
                {
                    Text = parts[i].name,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var btn = new Button
                {
                    Content = btnContent,
                    Tag = parts[i].fullPath,
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD4)),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(4, 1, 4, 1),
                    Cursor = Cursors.Hand,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                btn.Click += BreadcrumbSegment_Click;

                // Hover effect
                btn.MouseEnter += (s, e) =>
                {
                    if (s is Button b) b.Foreground = new SolidColorBrush(Color.FromRgb(0x0, 0x78, 0xD4));
                };
                btn.MouseLeave += (s, e) =>
                {
                    if (s is Button b) b.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD4));
                };

                BreadcrumbBar.Items.Add(btn);
            }
        }
        catch
        {
            // パース失敗時はパスをそのまま表示
            var text = new TextBlock
            {
                Text = path,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD4)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 1, 4, 1),
                FontSize = 11
            };
            BreadcrumbBar.Items.Add(text);
        }
    }

    private async void BreadcrumbSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path && ViewModel is { } vm)
        {
            await vm.NavigateToAsync(path);
        }
    }

    private void AddressBarBorder_Click(object sender, MouseButtonEventArgs e)
    {
        // パンくずボタン以外（空白部分）をクリックしたら編集モードに切り替え
        // ボタンのクリックは BreadcrumbSegment_Click で処理される
        if (e.OriginalSource is not Button)
        {
            SwitchToEditMode();
            e.Handled = true;
        }
    }

    private void SwitchToEditMode()
    {
        BreadcrumbBar.Visibility = Visibility.Collapsed;
        AddressTextBox.Visibility = Visibility.Visible;
        AddressTextBox.Focus();
        AddressTextBox.SelectAll();
    }

    private void SwitchToBreadcrumbMode()
    {
        AddressTextBox.Visibility = Visibility.Collapsed;
        BreadcrumbBar.Visibility = Visibility.Visible;
    }

    private async void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ViewModel is { } vm)
            {
                await vm.OnAddressBarEnter();
                SwitchToBreadcrumbMode();
            }
        }
        else if (e.Key == Key.Escape)
        {
            SwitchToBreadcrumbMode();
        }
    }

    private void AddressBar_LostFocus(object sender, RoutedEventArgs e)
    {
        SwitchToBreadcrumbMode();
    }

    private async void FilePaneControl_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel == null) return;
        if (e.ChangedButton == MouseButton.XButton1)
        {
            await ViewModel.GoBack();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            await ViewModel.GoForward();
            e.Handled = true;
        }
    }

    private async void ListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView listView && listView.SelectedItem is FileItem item)
        {
            if (ViewModel is { } vm)
            {
                await vm.OnItemDoubleClicked(item);
            }
        }
        else if (ViewModel is { } vm)
        {
            var parent = Directory.GetParent(vm.CurrentPath);
            if (parent != null)
            {
                await vm.NavigateToAsync(parent.FullName);
            }
        }
    }

    private async void UserControl_KeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;

        // Ctrl+F → open Command Palette (bubbles up to MainWindow)
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Let it bubble up to MainWindow which handles Ctrl+F → ToggleCommandPalette
            return;
        }

        bool isTextBoxFocused = e.OriginalSource is TextBox;

        if (e.Key == Key.F5)
        {
            await vm.Refresh();
            e.Handled = true;
            return;
        }

        if (isTextBoxFocused) return;

        if (e.Key == Key.F2)
        {
            await vm.RenameItem(vm.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            await vm.DeleteItems(FileListView.SelectedItems);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.CopyItems(FileListView.SelectedItems);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.X && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.CutItems(FileListView.SelectedItems);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            await vm.PasteItems();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            await vm.OpenItem(vm.SelectedItem);
            e.Handled = true;
            return;
        }
    }

    // Context menu handlers
    /// <summary>ブックマーク追加対象のパスを一時保持</summary>
    public string? PendingBookmarkPath { get; set; }

    // ==================== Native Shell Context Menu ====================

    private void FileListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Select the item under cursor on right-click down, suppress WPF context menu
        var hitElement = e.OriginalSource as DependencyObject;
        var listViewItem = FindAncestor<ListViewItem>(hitElement);
        if (listViewItem != null && listViewItem.Content is FileItem item)
        {
            if (!FileListView.SelectedItems.Contains(item))
            {
                FileListView.SelectedItem = item;
            }
        }
        // Don't set e.Handled here - let the Up event handle the menu
    }

    private void FileListView_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;

        e.Handled = true;

        var window = Window.GetWindow(this);
        if (window == null) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var screenPos = PointToScreen(e.GetPosition(this));
        int x = (int)screenPos.X;
        int y = (int)screenPos.Y;

        // Check if click is on a ListViewItem
        var hitElement = e.OriginalSource as DependencyObject;
        var listViewItem = FindAncestor<ListViewItem>(hitElement);

        if (listViewItem != null && FileListView.SelectedItems.Count > 0)
        {
            // Item context menu
            var paths = FileListView.SelectedItems
                .OfType<FileItem>()
                .Select(f => f.FullPath)
                .ToArray();

            if (paths.Length == 0) return;

            var customItems = new List<SnapMenuItem>();

            // Add "Bookmark" for folders
            var selectedItem = vm.SelectedItem;
            if (selectedItem is { IsDirectory: true } folder)
            {
                var bookmarkPath = folder.FullPath;
                customItems.Add(new SnapMenuItem("ブックマークに追加", () =>
                {
                    PendingBookmarkPath = bookmarkPath;
                    RaiseEvent(new RoutedEventArgs(AddBookmarkRequestedEvent, this));
                }));
            }

            // Disable AllowDrop on all panes to prevent drag-drop during menu pump
            var allPanes = GetAllFilePanes();
            foreach (var pane in allPanes) pane.SetAllowDrop(false);
            _shellMenuOpen = true;
            try
            {
                ShellContextMenu.ShowContextMenu(hwnd, paths, x, y,
                    onRefresh: () => Dispatcher.BeginInvoke(async () => { if (vm != null) await vm.Refresh(); }),
                    customItems: customItems);
            }
            finally
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
                {
                    _shellMenuOpen = false;
                    foreach (var pane in allPanes) pane.SetAllowDrop(true);
                });
            }
        }
        else
        {
            // Background context menu
            var folderPath = vm.CurrentPath;
            if (string.IsNullOrEmpty(folderPath)) return;

            var customItems = new List<SnapMenuItem>
            {
                new("ブックマークに追加", () =>
                {
                    PendingBookmarkPath = folderPath;
                    RaiseEvent(new RoutedEventArgs(AddBookmarkRequestedEvent, this));
                }),
                new("更新", () => Dispatcher.BeginInvoke(async () => { if (vm != null) await vm.Refresh(); })),
            };

            var allPanes2 = GetAllFilePanes();
            foreach (var pane in allPanes2) pane.SetAllowDrop(false);
            _shellMenuOpen = true;
            try
            {
                ShellContextMenu.ShowBackgroundMenu(hwnd, folderPath, x, y,
                    onRefresh: () => Dispatcher.BeginInvoke(async () => { if (vm != null) await vm.Refresh(); }),
                    customItems: customItems);
            }
            finally
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
                {
                    _shellMenuOpen = false;
                    foreach (var pane in allPanes2) pane.SetAllowDrop(true);
                });
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T result) return result;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void SetAllowDrop(bool allow)
    {
        FileListView.AllowDrop = allow;
        if (Parent is Grid grid)
            grid.AllowDrop = allow;
    }

    private List<FilePaneControl> GetAllFilePanes()
    {
        var window = Window.GetWindow(this);
        if (window == null) return new() { this };
        return FindDescendants<FilePaneControl>(window);
    }

    private static List<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var results = new List<T>();
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) results.Add(t);
            results.AddRange(FindDescendants<T>(child));
        }
        return results;
    }

    // --- File/Folder Drag & Drop ---

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _fileDragStartPoint = e.GetPosition(FileListView);
        _fileDragInProgress = false;
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _fileDragInProgress)
            return;

        var pos = e.GetPosition(FileListView);
        var diff = pos - _fileDragStartPoint;
        if (Math.Abs(diff.X) < FileDragThreshold && Math.Abs(diff.Y) < FileDragThreshold)
            return;

        // Get items to drag: use selected items from ListView
        var selectedItems = FileListView.SelectedItems.OfType<FileItem>()
            .Where(f => f.Name != "..")
            .ToList();

        if (selectedItems.Count == 0) return;

        _fileDragInProgress = true;

        var paths = selectedItems.Select(f => f.FullPath).ToArray();
        // FileDrop 形式のみ使用（ペイン間で確実に動作する）
        var data = new DataObject(DataFormats.FileDrop, paths);

        // Show drag ghost with icon
        var ghostText = selectedItems.Count == 1
            ? selectedItems[0].Name
            : $"{selectedItems.Count} 項目";
        var ghostIcon = selectedItems.Count == 1 ? selectedItems[0].Icon : null;
        _dragGhost.Show(ghostText, ghostIcon);

        FileListView.GiveFeedback += FileList_GiveFeedback;
        try
        {
            DragDrop.DoDragDrop(FileListView, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            FileListView.GiveFeedback -= FileList_GiveFeedback;
            _dragGhost.Close();
            _fileDragInProgress = false;
        }
    }

    private void FileList_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        _dragGhost.UpdatePosition();
    }

    private void FileList_DragEnter(object sender, DragEventArgs e)
    {
        FileList_HandleDragOver(sender, e);
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        FileList_HandleDragOver(sender, e);
    }

    private void FileList_HandleDragOver(object sender, DragEventArgs e)
    {
        if (_shellMenuOpen) { e.Effects = DragDropEffects.None; e.Handled = true; return; }

        e.Effects = DragDropEffects.None;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Handled = true;
            return;
        }

        // Check if over a folder item
        var target = GetFileItemUnderMouse(e);
        if (target is { IsDirectory: true })
        {
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            // Allow drop to current directory (move into this pane's current folder)
            if (ViewModel != null)
                e.Effects = DragDropEffects.Move;
        }

        e.Handled = true;
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        // Suppress drop events while shell context menu is open
        if (_shellMenuOpen) { e.Handled = true; return; }

        var vm = ViewModel;
        if (vm == null) return;

        // Only handle file drops initiated by Snap's own drag (not from shell context menu)
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        // Verify this is a genuine user-initiated drag (not a phantom drop from shell menu)
        if (e.AllowedEffects == DragDropEffects.None) { e.Handled = true; return; }

        string[]? sourcePaths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (sourcePaths == null || sourcePaths.Length == 0) return;

        // Determine target folder
        string targetFolder;
        var targetItem = GetFileItemUnderMouse(e);
        if (targetItem is { IsDirectory: true })
        {
            targetFolder = targetItem.FullPath;
        }
        else
        {
            targetFolder = vm.CurrentPath;
        }

        // Don't drop onto the same folder the items are already in
        if (sourcePaths.All(p =>
            string.Equals(Path.GetDirectoryName(p), targetFolder, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Perform move
        int successCount = 0;
        int errorCount = 0;
        var errors = new System.Collections.Generic.List<string>();

        try
        {
            await Task.Run(() =>
            {
                foreach (var sourcePath in sourcePaths)
                {
                    try
                    {
                        var name = Path.GetFileName(sourcePath);
                        var destPath = Path.Combine(targetFolder, name);

                        // Skip if source == dest
                        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Handle name collision
                        if (File.Exists(destPath) || Directory.Exists(destPath))
                        {
                            var nameNoExt = Path.GetFileNameWithoutExtension(name);
                            var ext = Path.GetExtension(name);
                            int counter = 2;
                            do
                            {
                                destPath = Path.Combine(targetFolder, $"{nameNoExt} ({counter}){ext}");
                                counter++;
                            } while (File.Exists(destPath) || Directory.Exists(destPath));
                        }

                        if (Directory.Exists(sourcePath))
                            Directory.Move(sourcePath, destPath);
                        else if (File.Exists(sourcePath))
                            File.Move(sourcePath, destPath);

                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        errors.Add($"{Path.GetFileName(sourcePath)}: {ex.Message}");
                    }
                }
            });

            if (errorCount > 0)
            {
                vm.StatusMessage = $"{successCount} 項目を移動、{errorCount} 項目でエラー: {string.Join("; ", errors.Take(3))}";
            }
            else if (successCount > 0)
            {
                vm.StatusMessage = $"{successCount} 項目を移動しました";
            }

            // Refresh all panes that might be affected
            try { await vm.Refresh(); } catch { }
            try { await RefreshOtherPanesShowingPaths(sourcePaths, vm); } catch { }
        }
        catch (Exception ex)
        {
            // 移動自体のエラーのみ表示（リフレッシュエラーは無視）
            vm.StatusMessage = $"エラー: {ex.Message}";
        }

        e.Handled = true;
    }

    /// <summary>
    /// マウス位置の FileItem を取得する。ListView のアイテム上にない場合は null。
    /// </summary>
    private FileItem? GetFileItemUnderMouse(DragEventArgs e)
    {
        var pos = e.GetPosition(FileListView);
        var hitResult = VisualTreeHelper.HitTest(FileListView, pos);
        if (hitResult?.VisualHit == null) return null;

        // Walk up the visual tree to find a ListViewItem
        DependencyObject? current = hitResult.VisualHit;
        while (current != null && current is not ListViewItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is ListViewItem lvi && lvi.Content is FileItem item)
            return item;

        return null;
    }

    /// <summary>
    /// ドラッグ元のフォルダを表示している他のペインを更新する。
    /// </summary>
    private async Task RefreshOtherPanesShowingPaths(string[] sourcePaths, FilePaneViewModel excludeVm)
    {
        // Collect unique source directories
        var sourceDirs = sourcePaths
            .Select(p => Path.GetDirectoryName(p))
            .Where(d => d != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceDirs.Count == 0) return;

        // Walk up the visual tree to find the Window, then find all FilePaneViewModels
        var window = Window.GetWindow(this);
        if (window?.DataContext is not ViewModels.MainViewModel mainVm) return;

        var allPanes = new[]
        {
            mainVm.TopLeftPane, mainVm.TopRightPane,
            mainVm.BottomLeftPane, mainVm.BottomRightPane
        };

        foreach (var pane in allPanes)
        {
            if (pane?.SelectedTab == null || pane.SelectedTab == excludeVm) continue;

            var tab = pane.SelectedTab;
            if (sourceDirs.Any(d => string.Equals(d, tab.CurrentPath, StringComparison.OrdinalIgnoreCase)))
            {
                await tab.Refresh();
            }
        }
    }
}
