using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Snap.Helpers;
using Snap.Interop;
using Snap.Models;
using Snap.Services;
using Snap.ViewModels;
using System.Linq;

namespace Snap.Views;

public partial class FilePaneControl : UserControl
{
    /// <summary>True while native shell context menu is open (suppress drop events across all panes).</summary>
    private static bool _shellMenuOpen;
    /// <summary>True from right-click until the shell menu flow finishes (prevents a second menu).</summary>
    private static bool _menuBusy;
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

    /// <summary>Asks the window to open a folder in a new tab next to this one
    /// (<see cref="OpenInNewTabRequestedEventArgs"/>); handled by MainViewModel.OpenInNewTab (#14).</summary>
    public static readonly RoutedEvent OpenInNewTabRequestedEvent =
        EventManager.RegisterRoutedEvent("OpenInNewTabRequested", RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(FilePaneControl));

    // Column header display name → sort property mapping
    private static readonly Dictionary<string, string> ColumnMap = new()
    {
        { "名前", "Name" },
        { "更新日時", "LastModified" },
        { "サイズ", "Size" },
        { "種類", "Type" },
    };

    // Reverse mapping for indicator updates
    private static readonly Dictionary<string, string> ColumnDisplayNames = new()
    {
        { "Name", "名前" },
        { "LastModified", "更新日時" },
        { "Size", "サイズ" },
        { "Type", "種類" },
    };

    public FilePaneControl()
    {
        InitializeComponent();
        MouseDown += FilePaneControl_MouseDown;
        DataContextChanged += OnDataContextChanged;
        AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
        TrackListFocus();
    }

    private FilePaneViewModel? ViewModel => DataContext as FilePaneViewModel;

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Role == GridViewColumnHeaderRole.Padding) return;
        if (header.Column == null) return;

        // Strip existing sort indicator from header text
        var headerText = (header.Column.Header?.ToString() ?? "").TrimEnd(' ', '^', 'v');

        if (!ColumnMap.TryGetValue(headerText, out var sortProperty)) return;

        ViewModel?.SortByColumn(sortProperty);
        UpdateSortIndicators();
    }

    private void UpdateSortIndicators()
    {
        var vm = ViewModel;
        if (vm == null) return;

        var gridView = FileListView.View as GridView;
        if (gridView == null) return;

        foreach (var col in gridView.Columns)
        {
            var text = (col.Header?.ToString() ?? "").TrimEnd(' ', '^', 'v');
            if (!ColumnMap.TryGetValue(text, out var prop)) continue;

            if (prop == vm.SortColumn)
            {
                col.Header = $"{text} {(vm.SortAscending ? "^" : "v")}";
            }
            else
            {
                col.Header = text;
            }
        }
    }

    // The folder shown before the last CurrentPath change: after going up, it is re-selected.
    private string? _shownPath;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FilePaneViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            oldVm.RevealRequested -= OnRevealRequested;
            oldVm.ItemsReplacing -= OnItemsReplacing;
            oldVm.ItemsReplaced -= OnItemsReplaced;
        }

        if (e.NewValue is FilePaneViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            newVm.RevealRequested += OnRevealRequested;
            newVm.ItemsReplacing += OnItemsReplacing;
            newVm.ItemsReplaced += OnItemsReplaced;
            RebuildBreadcrumb(newVm.CurrentPath);
            _shownPath = newVm.CurrentPath;
            // Tab switch (Ctrl+Tab etc.): if the list had the focus, it keeps it.
            if (ListShouldKeepFocus())
                FocusListAfterLayout(select: null);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FilePaneViewModel vm) return;
        if (e.PropertyName == nameof(FilePaneViewModel.CurrentPath))
        {
            RebuildBreadcrumb(vm.CurrentPath);
            var previous = _shownPath;
            _shownPath = vm.CurrentPath;
            // Keyboard navigation (Backspace, Enter, Alt+arrows) must leave the focus in the list:
            // re-select the folder we came up from (like Explorer), else focus the first entry.
            if (ListShouldKeepFocus())
            {
                var cameFrom = previous != null
                    && string.Equals(FilePaneViewModel.GetParentPath(previous), vm.CurrentPath, StringComparison.OrdinalIgnoreCase)
                    ? vm.Items.FirstOrDefault(i => string.Equals(i.FullPath.TrimEnd('\\'), previous.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    : null;
                FocusListAfterLayout(cameFrom, newFolder: true);
            }
        }
        else if (e.PropertyName == nameof(FilePaneViewModel.IsFilterVisible) && vm.IsFilterVisible)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                FilterTextBox.Focus();
                FilterTextBox.SelectAll();
            });
        }
    }

    /// <summary>
    /// True when the keyboard focus is in this pane's list, or was lost because the focused row
    /// went away with the old items (focus is then nowhere / on the window). A text box or
    /// another pane/tree keeps its focus.
    /// </summary>
    private bool ListShouldKeepFocus()
    {
        if (FileListView.IsKeyboardFocusWithin) return true;
        var focused = Keyboard.FocusedElement as DependencyObject;
        return (focused == null || focused is Window) && _listOwnsFocus;
    }

    // True from the moment the list (or a row) gets the keyboard focus until the focus moves
    // somewhere else on purpose. A focused row that disappears with the old items does not
    // clear it (its LostKeyboardFocus no longer reaches the list).
    private bool _listOwnsFocus;

    private void TrackListFocus()
    {
        FileListView.GotKeyboardFocus += (_, _) => _listOwnsFocus = true;
        FileListView.LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is DependencyObject d && d is not Window
                && !(d is Visual v && FileListView.IsAncestorOf(v)))
                _listOwnsFocus = false;
        };
    }

    /// <summary>Focuses the list: <paramref name="select"/> selected and focused, else the
    /// selected row, else the first row (focused, not selected), else the list itself.</summary>
    public void FocusList() => FocusListAfterLayout(select: null);

    /// <param name="newFolder">The list now shows another folder: start with nothing selected
    /// except <paramref name="select"/>. Done after layout, because a recycled row that was
    /// selected re-selects whatever item it is given next.</param>
    private void FocusListAfterLayout(FileItem? select, bool newFolder = false)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            try
            {
                if (newFolder)
                {
                    FileListView.UpdateLayout();
                    FileListView.UnselectAll();
                }
                if (select != null)
                    FileListView.SelectedItem = select;
                var hadSelection = FileListView.SelectedItems.Count > 0;
                var target = select ?? FileListView.SelectedItem as FileItem
                             ?? (FileListView.Items.Count > 0 ? FileListView.Items[0] as FileItem : null);
                if (target == null)
                {
                    FileListView.Focus();
                    return;
                }
                FileListView.ScrollIntoView(target);
                FileListView.UpdateLayout();
                if (FileListView.ItemContainerGenerator.ContainerFromItem(target) is ListViewItem row)
                {
                    row.Focus();
                    // ListBox selects a row that gets the focus from inside the list
                    // (OnGotKeyboardFocus → MakeSingleSelection). Nothing selected stays nothing
                    // selected, as in Explorer: the row only has the focus.
                    if (!hadSelection)
                        FileListView.UnselectAll();
                }
                else
                    FileListView.Focus();
            }
            catch (Exception ex) { Log.Warn("FilePane.FocusList", "focus not restored", ex); }
        });
    }

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection) return;
        ViewModel?.SetSelection(FileListView.SelectedItems);
    }

    // ==================== List swapped in the same folder (#16) ====================
    // Sort, F5, a file operation and the folder watcher swap the whole list in one step. The
    // scroll position, the focused row and the selection are carried over by path.

    private double _savedVerticalOffset;
    private bool _savedListHadFocus;
    private string? _savedFocusedPath;
    private string? _savedFolder;
    private bool _restoringSelection;

    private void OnItemsReplacing()
    {
        _savedVerticalOffset = FindListScrollViewer()?.VerticalOffset ?? 0;
        _savedListHadFocus = ListShouldKeepFocus();
        _savedFocusedPath = FileListView.IsKeyboardFocusWithin
                            && Keyboard.FocusedElement is ListViewItem { DataContext: FileItem focused }
            ? focused.FullPath
            : null;
        _savedFolder = ViewModel?.CurrentPath;
    }

    private void OnItemsReplaced(IReadOnlyList<FileItem> reselect)
    {
        var folder = _savedFolder;
        var offset = _savedVerticalOffset;
        var hadFocus = _savedListHadFocus;
        var focusedPath = _savedFocusedPath;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            try
            {
                var vm = ViewModel;
                // Moved to another folder meanwhile: the CurrentPath handler owns the view then.
                if (vm == null || !FileSystemService.SamePath(vm.CurrentPath, folder)) return;

                FindListScrollViewer()?.ScrollToVerticalOffset(offset);

                if (hadFocus)
                {
                    FileListView.UpdateLayout();
                    var focusItem = (focusedPath != null
                                        ? vm.Items.FirstOrDefault(i => FileSystemService.SamePath(i.FullPath, focusedPath))
                                        : null)
                                    ?? reselect.FirstOrDefault();
                    if (focusItem != null
                        && FileListView.ItemContainerGenerator.ContainerFromItem(focusItem) is ListViewItem row)
                        row.Focus();
                    else
                        FileListView.Focus();
                }

                RestoreSelection(reselect);
            }
            catch (Exception ex) { Log.Warn("FilePane.KeepView", "selection / scroll not restored", ex); }
        });
    }

    /// <summary>Selects exactly <paramref name="items"/> (reports the selection to the view model once).</summary>
    private void RestoreSelection(IReadOnlyList<FileItem> items)
    {
        var current = new HashSet<object>(FileListView.SelectedItems.Cast<object>(), ReferenceEqualityComparer.Instance);
        if (current.Count == items.Count && items.All(current.Contains)) return;

        _restoringSelection = true;
        try
        {
            if (items.Count > 0 && items.Count == FileListView.Items.Count)
            {
                FileListView.SelectAll();
            }
            else
            {
                FileListView.UnselectAll();
                foreach (var item in items)
                    FileListView.SelectedItems.Add(item);
            }
        }
        finally
        {
            _restoringSelection = false;
        }
        ViewModel?.SetSelection(FileListView.SelectedItems);
    }

    private ScrollViewer? FindListScrollViewer()
    {
        DependencyObject? node = FileListView;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(node);
        while (queue.Count > 0)
        {
            node = queue.Dequeue();
            if (node is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }

    /// <summary>
    /// After a rename / new folder (#15): selects <paramref name="item"/> alone and scrolls to it.
    /// The list takes the keyboard focus back only if it had it (the dialog took it meanwhile).
    /// </summary>
    private void OnRevealRequested(FileItem item)
    {
        if (ListShouldKeepFocus())
        {
            FocusListAfterLayout(item, newFolder: true);
            return;
        }
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            try
            {
                FileListView.UnselectAll();
                FileListView.SelectedItem = item;
                FileListView.ScrollIntoView(item);
            }
            catch (Exception ex) { Log.Warn("FilePane.Reveal", item.FullPath, ex); }
        });
    }

    // ==================== Filter bar (Ctrl+Shift+F, #14) ====================

    private void FilterTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ViewModel?.ClearFilter();
                FocusList();
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Down:
                FocusList();
                e.Handled = true;
                break;
        }
    }

    private void FilterTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // An empty filter bar has nothing to show; hide it when the user leaves it.
        if (ViewModel is { } vm && string.IsNullOrEmpty(vm.FilterText))
            vm.IsFilterVisible = false;
    }

    private void RebuildBreadcrumb(string path)
    {
        BreadcrumbBar.Items.Clear();

        if (string.IsNullOrEmpty(path)) return;

        // PC view — just show "PC"
        if (path == FilePaneViewModel.PcViewPath)
        {
            AddBreadcrumbButton("PC", FilePaneViewModel.PcViewPath, null);
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var parts = new List<(string name, string fullPath)>();

            // Always start with PC
            parts.Add(("PC", FilePaneViewModel.PcViewPath));

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
                ImageSource? segIcon = null;
                if (parts[i].fullPath != FilePaneViewModel.PcViewPath)
                {
                    try { (segIcon, _) = IconHelper.GetIconAndType(parts[i].fullPath, true); }
                    catch (Exception ex) { Log.Warn("FilePane.BreadcrumbIcon", parts[i].fullPath, ex); }
                }
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
        catch (Exception ex)
        {
            // パース失敗時はパスをそのまま表示
            Log.Warn("FilePane.Breadcrumb", path, ex);
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

    private void AddBreadcrumbButton(string text, string navPath, ImageSource? icon)
    {
        var btnContent = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null)
        {
            btnContent.Children.Add(new Image
            {
                Source = icon, Width = 14, Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            });
        }
        btnContent.Children.Add(new TextBlock
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center
        });

        var btn = new Button
        {
            Content = btnContent,
            Tag = navPath,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD4)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 1, 4, 1),
            Cursor = Cursors.Hand,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        btn.Click += BreadcrumbSegment_Click;
        btn.MouseEnter += (s, _) => { if (s is Button b) b.Foreground = new SolidColorBrush(Color.FromRgb(0x0, 0x78, 0xD4)); };
        btn.MouseLeave += (s, _) => { if (s is Button b) b.Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD4)); };
        BreadcrumbBar.Items.Add(btn);
    }

    private async void BreadcrumbSegment_Click(object sender, RoutedEventArgs e)
    {
        // async void event handler: nothing may escape to the dispatcher (#13).
        try { await BreadcrumbSegment_ClickAsync(sender, e); }
        catch (Exception ex) { Log.UserError("FilePane.BreadcrumbSegment_Click", "操作に失敗しました", ex); }
    }

    private async Task BreadcrumbSegment_ClickAsync(object sender, RoutedEventArgs e)
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

    /// <summary>Focus the address bar in edit mode (click on blank area, Ctrl+L, Alt+D, F4).</summary>
    public void SwitchToEditMode()
    {
        ViewModel?.ResetAddressText();
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
        // async void event handler: nothing may escape to the dispatcher (#13).
        try { await AddressBar_KeyDownAsync(sender, e); }
        catch (Exception ex) { Log.UserError("FilePane.AddressBar_KeyDown", "操作に失敗しました", ex); }
    }

    private async Task AddressBar_KeyDownAsync(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (ViewModel is { } vm)
            {
                await vm.OnAddressBarEnter();
                SwitchToBreadcrumbMode();
                FileListView.Focus();
            }
        }
        else if (e.Key == Key.Escape)
        {
            // Discard the edit: the address goes back to the committed CurrentPath.
            e.Handled = true;
            ViewModel?.ResetAddressText();
            SwitchToBreadcrumbMode();
            FileListView.Focus();
        }
    }

    private void AddressBar_LostFocus(object sender, RoutedEventArgs e)
    {
        ViewModel?.ResetAddressText();
        SwitchToBreadcrumbMode();
    }

    private async void FilePaneControl_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // async void event handler: nothing may escape to the dispatcher (#13).
        try { await FilePaneControl_MouseDownAsync(sender, e); }
        catch (Exception ex) { Log.UserError("FilePane.FilePaneControl_MouseDown", "操作に失敗しました", ex); }
    }

    private async Task FilePaneControl_MouseDownAsync(object sender, MouseButtonEventArgs e)
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
        // async void event handler: nothing may escape to the dispatcher (#13).
        try { await ListView_MouseDoubleClickAsync(sender, e); }
        catch (Exception ex) { Log.UserError("FilePane.ListView_MouseDoubleClick", "操作に失敗しました", ex); }
    }

    private async Task ListView_MouseDoubleClickAsync(object sender, MouseButtonEventArgs e)
    {
        // ダブルクリック位置がアイテム上かどうかを判定
        var hitElement = e.OriginalSource as DependencyObject;
        var listViewItem = FindAncestor<ListViewItem>(hitElement);

        if (listViewItem != null && listViewItem.Content is FileItem item)
        {
            if (item.IsDirectory && Keyboard.Modifiers == ModifierKeys.Control)
            {
                RequestOpenInNewTab(item.FullPath, select: true);
                e.Handled = true;
                return;
            }
            if (ViewModel is { } vm)
            {
                await vm.OnItemDoubleClicked(item);
            }
        }
        else if (ViewModel is { } vm)
        {
            // Blank area: go up (drive root → PC view; nothing above the PC view)
            await vm.GoUpAsync();
        }
    }

    // Middle-click on a folder: open it in a new tab in the background (like a browser link).
    private void FileListView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.Content is FileItem { IsDirectory: true } folder)
        {
            RequestOpenInNewTab(folder.FullPath, select: false);
            e.Handled = true;
        }
    }

    private void RequestOpenInNewTab(string path, bool select)
    {
        if (ViewModel is { } vm)
            RaiseEvent(new OpenInNewTabRequestedEventArgs(OpenInNewTabRequestedEvent, this, vm, path, select));
    }

    private async void UserControl_KeyDown(object sender, KeyEventArgs e)
    {
        // async void event handler: nothing may escape to the dispatcher (#13).
        try { await UserControl_KeyDownAsync(sender, e); }
        catch (Exception ex) { Log.UserError("FilePane.UserControl_KeyDown", "操作に失敗しました", ex); }
    }

    private async Task UserControl_KeyDownAsync(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;

        // Ctrl+F → open Command Palette (bubbles up to MainWindow)
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Let it bubble up to MainWindow which handles Ctrl+F → ToggleCommandPalette
            return;
        }

        // Ctrl+L / Alt+D / F4 → address bar (same as Explorer)
        if ((e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
            || (e.Key == Key.System && e.SystemKey == Key.D && Keyboard.Modifiers == ModifierKeys.Alt)
            || (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.None))
        {
            SwitchToEditMode();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+F → filter bar for this folder (#14)
        if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            vm.ShowFilter();
            // Already shown (IsFilterVisible unchanged): just go back to it.
            FilterTextBox.Focus();
            FilterTextBox.SelectAll();
            e.Handled = true;
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

        // Backspace → up
        if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await vm.GoUpAsync();
            return;
        }

        // Alt+Left / Alt+Right / Alt+Up. Window-wide InputBindings cover them elsewhere, but the
        // ListView consumes Alt+Left/Right before they bubble up, so the list handles them here.
        if (e.Key == Key.System && Keyboard.Modifiers == ModifierKeys.Alt
            && e.SystemKey is Key.Left or Key.Right or Key.Up)
        {
            e.Handled = true;
            await (e.SystemKey switch
            {
                Key.Left => vm.GoBack(),
                Key.Right => vm.GoForward(),
                _ => vm.GoUpAsync(),
            });
            return;
        }

        // Alt+Enter → Properties
        if (e.Key == Key.System && e.SystemKey == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            e.Handled = true;
            vm.ShowProperties(FileListView.SelectedItems.Count > 0 ? vm.SelectedItem : null);
            return;
        }

        // Ctrl+Shift+C → full paths as text
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            vm.CopyFullPaths(FileListView.SelectedItems);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2)
        {
            await vm.RenameItem(vm.SelectedItem);
            e.Handled = true;
            return;
        }

        // Delete → recycle bin; Shift+Delete → delete for good (#15)
        if (e.Key == Key.Delete && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            e.Handled = true;
            if (Keyboard.Modifiers == ModifierKeys.Shift)
                await vm.DeleteItemsPermanently(FileListView.SelectedItems);
            else
                await vm.DeleteItems(FileListView.SelectedItems);
            return;
        }

        // Ctrl+Shift+N → new folder, then rename it right away (#15)
        if (e.Key == Key.N && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            await vm.NewFolderAsync();
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

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await vm.OpenSelection(FileListView.SelectedItems);
            return;
        }
    }

    // Context menu handlers
    /// <summary>ブックマーク追加対象のパスを一時保持</summary>
    public string? PendingBookmarkPath { get; set; }

    // ==================== Native Shell Context Menu ====================

    private void FileListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A menu is being prepared/shown: don't change the selection underneath it
        if (_menuBusy) { e.Handled = true; return; }

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

    private async void FileListView_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;

        e.Handled = true;

        // Never open two shell menus at once (same or another pane)
        if (_menuBusy) return;

        var window = Window.GetWindow(this);
        if (window == null) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Shift+right-click adds the extended verbs (e.g. "Open in new process", "Open PowerShell window here")
        uint extraFlags = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? ShellNativeMethods.CMF_EXTENDEDVERBS : 0;

        var screenPos = PointToScreen(e.GetPosition(this));
        int x = (int)screenPos.X;
        int y = (int)screenPos.Y;

        // Check if click is on a ListViewItem
        var hitElement = e.OriginalSource as DependencyObject;
        var listViewItem = FindAncestor<ListViewItem>(hitElement);

        string[]? paths = null;
        string? folderPath = null;
        List<SnapMenuItem> customItems;

        if (listViewItem != null && FileListView.SelectedItems.Count > 0)
        {
            // Item context menu
            paths = FileListView.SelectedItems
                .OfType<FileItem>()
                .Select(f => f.FullPath)
                .ToArray();

            if (paths.Length == 0) return;

            customItems = new List<SnapMenuItem>();

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
        }
        else
        {
            // Background context menu
            folderPath = vm.CurrentPath;
            if (string.IsNullOrEmpty(folderPath)) return;

            var bgPath = folderPath;
            customItems = new List<SnapMenuItem>
            {
                new("ブックマークに追加", () =>
                {
                    PendingBookmarkPath = bgPath;
                    RaiseEvent(new RoutedEventArgs(AddBookmarkRequestedEvent, this));
                }),
                new("更新", () => Dispatcher.BeginInvoke(() => vm?.Refresh().SafeFireAndForget("FilePane.Refresh", "更新できません"))),
            };
        }

        _menuBusy = true;
        List<FilePaneControl> allPanes = new();
        try
        {
            // Show wait cursor while shell extensions load (UI stays responsive meanwhile)
            Mouse.OverrideCursor = Cursors.Wait;

            // Disable AllowDrop on all panes to prevent drag-drop during menu pump
            allPanes = GetAllFilePanes();
            foreach (var pane in allPanes) pane.SetAllowDrop(false);
            _shellMenuOpen = true;

            // A shell command can change this folder, the items' folders and (paste of a cut) the
            // folders the clipboard files came from; the panes showing any of them refresh (#15).
            var clipboardFolders = FilePaneViewModel.ClipboardSourceFolders();
            var menuPaths = paths;
            Action onRefresh = () => Dispatcher.BeginInvoke(() => vm.RefreshAfterShellCommandAsync(menuPaths, clipboardFolders)
                .SafeFireAndForget("FilePane.Refresh", "更新できません"));
            Action onMenuReady = () => Mouse.OverrideCursor = null;

            if (paths != null)
                await ShellContextMenu.ShowContextMenuAsync(hwnd, paths, x, y,
                    onRefresh: onRefresh, customItems: customItems, onMenuReady: onMenuReady,
                    extraFlags: extraFlags);
            else
                await ShellContextMenu.ShowBackgroundMenuAsync(hwnd, folderPath!, x, y,
                    onRefresh: onRefresh, customItems: customItems, onMenuReady: onMenuReady,
                    extraFlags: extraFlags);
        }
        catch (Exception ex)
        {
            // async void: never let an exception escape to the dispatcher
            Log.UserError("FilePane.ContextMenu", "メニューを表示できません", ex);
        }
        finally
        {
            try
            {
                Mouse.OverrideCursor = null;
                var panes = allPanes;
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
                {
                    _shellMenuOpen = false;
                    foreach (var pane in panes) pane.SetAllowDrop(true);
                });
            }
            catch (Exception ex) { Log.Warn("FilePane.ContextMenu", "restore after menu failed", ex); }
            _menuBusy = false;
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
        if (_shellMenuOpen) return;
        _fileDragStartPoint = e.GetPosition(FileListView);
        _fileDragInProgress = false;
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_shellMenuOpen) return;
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

        // Ctrl 押下でコピー、それ以外は移動（Drop 側と同じ判定）
        var effect = (e.KeyStates & DragDropKeyStates.ControlKey) != 0
            ? DragDropEffects.Copy
            : DragDropEffects.Move;

        // Check if over a folder item
        var target = GetFileItemUnderMouse(e);
        if (target is { IsDirectory: true })
        {
            e.Effects = effect;
        }
        else
        {
            // Allow drop to current directory (into this pane's current folder)
            if (ViewModel != null)
                e.Effects = effect;
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

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } sourcePaths) return;
        e.Handled = true;

        // Hit test only: a folder under the mouse is the target, else this pane's folder.
        // Ctrl = copy, otherwise move. The operation itself is the view model's (#15).
        var targetFolder = GetFileItemUnderMouse(e) is { IsDirectory: true } folder ? folder.FullPath : vm.CurrentPath;
        bool isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;

        // async void event handler: nothing may escape to the dispatcher (#13).
        try { await vm.DropFilesAsync(sourcePaths, targetFolder, isCopy); }
        catch (Exception ex) { Log.UserError("FilePane.Drop", $"ドロップできません（{targetFolder}）", ex); }
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
}

/// <summary>Open <see cref="Path"/> in a new tab next to <see cref="From"/> (#14).</summary>
public sealed class OpenInNewTabRequestedEventArgs(
    RoutedEvent routedEvent, object source, FilePaneViewModel from, string path, bool select)
    : RoutedEventArgs(routedEvent, source)
{
    public FilePaneViewModel From { get; } = from;
    public string Path { get; } = path;
    public bool Select { get; } = select;
}
