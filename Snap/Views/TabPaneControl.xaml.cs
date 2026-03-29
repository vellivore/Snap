using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snap.Helpers;
using Snap.ViewModels;
using System.Linq;

namespace Snap.Views;

public partial class TabPaneControl : UserControl
{
    private Button? _addButton;

    // Tab drag & drop state
    private Point _tabDragStartPoint;
    private bool _tabDragInProgress;
    private Border? _tabDragSource;
    private const double TabDragThreshold = 5.0;
    private readonly DragGhostHelper _dragGhost = new();

    public TabPaneControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TabPaneViewModel oldVm)
        {
            oldVm.Tabs.CollectionChanged -= OnTabsChanged;
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is TabPaneViewModel newVm)
        {
            newVm.Tabs.CollectionChanged += OnTabsChanged;
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            RebuildTabHeaders();
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildTabHeaders();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabPaneViewModel.SelectedTab))
        {
            UpdateTabHighlights();
        }
    }

    private void RebuildTabHeaders()
    {
        TabHeaderPanel.Children.Clear();

        if (DataContext is not TabPaneViewModel vm) return;

        foreach (var tab in vm.Tabs)
        {
            var header = CreateTabHeader(tab);
            TabHeaderPanel.Children.Add(header);
        }

        // 「+」ボタンをタブの直後に配置
        _addButton = new Button
        {
            Content = "＋",
            FontSize = 14,
            Width = 28,
            Height = 22,
            Margin = new Thickness(2, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x82)),
            Cursor = Cursors.Hand,
            ToolTip = "新しいタブを追加"
        };
        _addButton.SetBinding(Button.CommandProperty, new Binding("AddTabCommand"));
        TabHeaderPanel.Children.Add(_addButton);

        UpdateTabHighlights();
    }

    private Border CreateTabHeader(FilePaneViewModel tab)
    {
        var tabIcon = new Image
        {
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0)
        };
        // 初期アイコン設定
        var (initIcon, _) = IconHelper.GetIconAndType(tab.CurrentPath, true);
        tabIcon.Source = initIcon;
        // CurrentPath 変更時にアイコンを更新
        tab.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(FilePaneViewModel.CurrentPath) && s is FilePaneViewModel vm)
            {
                var (icon, _) = IconHelper.GetIconAndType(vm.CurrentPath, true);
                tabIcon.Source = icon;
            }
        };

        var textBlock = new TextBlock
        {
            MaxWidth = 140,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD4)),
            Margin = new Thickness(2, 0, 4, 0)
        };
        textBlock.SetBinding(TextBlock.TextProperty, new Binding("TabHeader") { Source = tab });

        var closeButton = new Button
        {
            Content = "✕",
            FontSize = 10,
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x82)),
            Cursor = Cursors.Hand,
            Tag = tab,
            ToolTip = "タブを閉じる"
        };
        closeButton.Click += CloseTab_Click;

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(tabIcon);
        stack.Children.Add(textBlock);
        stack.Children.Add(closeButton);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x40)),
            BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Margin = new Thickness(2, 1, 0, 0),
            Padding = new Thickness(4, 2, 4, 2),
            Cursor = Cursors.Hand,
            Tag = tab,
            Child = stack,
            AllowDrop = true
        };
        border.MouseLeftButtonDown += TabHeader_MouseLeftButtonDown;
        border.MouseRightButtonUp += TabHeader_MouseRightButtonUp;
        border.MouseMove += TabHeader_MouseMove;
        border.DragEnter += TabHeader_DragEnter;
        border.DragOver += TabHeader_DragOver;
        border.Drop += TabHeader_Drop;

        return border;
    }

    private void UpdateTabHighlights()
    {
        if (DataContext is not TabPaneViewModel vm) return;

        foreach (var child in TabHeaderPanel.Children)
        {
            if (child is Border border && border.Tag is FilePaneViewModel tab)
            {
                if (tab == vm.SelectedTab)
                {
                    border.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x22));
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
                }
                else
                {
                    border.Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2C));
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x40));
                }
            }
        }
    }

    private void TabHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is FilePaneViewModel tab
            && DataContext is TabPaneViewModel paneVm)
        {
            paneVm.SelectedTab = tab;
            _tabDragStartPoint = e.GetPosition(this);
            _tabDragSource = border;
            _tabDragInProgress = false;
        }
    }

    private void TabHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragSource == null)
            return;

        var pos = e.GetPosition(this);
        var diff = pos - _tabDragStartPoint;
        if (Math.Abs(diff.X) < TabDragThreshold && Math.Abs(diff.Y) < TabDragThreshold)
            return;

        if (_tabDragInProgress) return;
        _tabDragInProgress = true;

        // Show drag ghost with tab name + icon
        if (_tabDragSource.Tag is FilePaneViewModel tabVm)
        {
            var (icon, _) = Helpers.IconHelper.GetIconAndType(tabVm.CurrentPath, true);
            _dragGhost.Show(tabVm.TabHeader, icon);
        }

        _tabDragSource.GiveFeedback += TabHeader_GiveFeedback;
        var data = new DataObject("SnapTabDrag", _tabDragSource.Tag!);
        try
        {
            DragDrop.DoDragDrop(_tabDragSource, data, DragDropEffects.Move);
        }
        finally
        {
            if (_tabDragSource != null) _tabDragSource.GiveFeedback -= TabHeader_GiveFeedback;
            _dragGhost.Close();
            _tabDragInProgress = false;
            _tabDragSource = null;
        }
    }

    private void TabHeader_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        _dragGhost.UpdatePosition();
    }

    private void TabHeader_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("SnapTabDrag"))
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void TabHeader_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("SnapTabDrag"))
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void TabHeader_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("SnapTabDrag")) return;
        if (sender is not Border targetBorder || targetBorder.Tag is not FilePaneViewModel targetTab) return;
        if (DataContext is not TabPaneViewModel vm) return;

        var sourceTab = e.Data.GetData("SnapTabDrag") as FilePaneViewModel;
        if (sourceTab == null || sourceTab == targetTab) return;

        var sourceIndex = vm.Tabs.IndexOf(sourceTab);
        var targetIndex = vm.Tabs.IndexOf(targetTab);
        if (sourceIndex < 0 || targetIndex < 0) return;

        vm.Tabs.Move(sourceIndex, targetIndex);
        e.Handled = true;
    }

    private void TabHeader_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not FilePaneViewModel tab) return;
        e.Handled = true;

        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
        };

        var renameItem = new MenuItem
        {
            Header = "タブ名の変更",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
        };
        renameItem.Click += (_, _) =>
        {
            var dialog = new RenameDialog(tab.TabHeader)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
            {
                tab.TabHeader = dialog.NewName;
                tab.HasCustomTabHeader = true;
            }
        };
        menu.Items.Add(renameItem);

        var resetItem = new MenuItem
        {
            Header = "タブ名を戻す",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            IsEnabled = tab.HasCustomTabHeader,
        };
        resetItem.Click += (_, _) =>
        {
            tab.HasCustomTabHeader = false;
            // Regenerate name from current path
            var path = tab.CurrentPath;
            if (path == FilePaneViewModel.PcViewPath)
            {
                tab.TabHeader = "PC";
            }
            else
            {
                tab.TabHeader = System.IO.Path.GetFileName(
                    path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
                    is { Length: > 0 } name ? name : path;
            }
        };
        menu.Items.Add(resetItem);

        menu.PlacementTarget = border;
        menu.IsOpen = true;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button
            && button.Tag is FilePaneViewModel tab
            && DataContext is TabPaneViewModel paneVm)
        {
            paneVm.CloseTabCommand.Execute(tab);
        }
    }

    // --- Cross-pane tab drop (Grid level) ---

    private void Grid_TabDragEnter(object sender, DragEventArgs e)
    {
        // タブドラッグのみハンドル、それ以外は子要素（FilePaneControl）に通す
        if (e.Data.GetDataPresent("SnapTabDrag"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void Grid_TabDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("SnapTabDrag"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void Grid_TabDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("SnapTabDrag")) return;
        if (DataContext is not TabPaneViewModel targetPaneVm) return;

        var sourceTab = e.Data.GetData("SnapTabDrag") as FilePaneViewModel;
        if (sourceTab == null) return;

        // 同じペイン内のタブ移動は TabHeader_Drop で処理済み
        if (targetPaneVm.Tabs.Contains(sourceTab)) return;

        // 元のペインを探して削除
        var window = Window.GetWindow(this);
        if (window?.DataContext is not ViewModels.MainViewModel mainVm) return;

        var allPanes = new[] { mainVm.TopLeftPane, mainVm.TopRightPane, mainVm.BottomLeftPane, mainVm.BottomRightPane };
        TabPaneViewModel? sourcePaneVm = null;
        foreach (var pane in allPanes)
        {
            if (pane.Tabs.Contains(sourceTab))
            {
                sourcePaneVm = pane;
                break;
            }
        }

        if (sourcePaneVm == null) return;

        // 元のペインの最後のタブは移動しない（空ペインになるため）
        if (sourcePaneVm.Tabs.Count <= 1) return;

        sourcePaneVm.Tabs.Remove(sourceTab);
        if (sourcePaneVm.SelectedTab == null && sourcePaneVm.Tabs.Count > 0)
            sourcePaneVm.SelectedTab = sourcePaneVm.Tabs[0];

        targetPaneVm.Tabs.Add(sourceTab);
        targetPaneVm.SelectedTab = sourceTab;

        e.Handled = true;
    }
}
