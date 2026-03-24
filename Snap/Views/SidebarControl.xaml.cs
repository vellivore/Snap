using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Snap.Helpers;
using Snap.Models;
using Snap.ViewModels;

namespace Snap.Views;

public partial class SidebarControl : UserControl
{
    private readonly DispatcherTimer _openTimer;
    private readonly DispatcherTimer _closeTimer;
    private readonly DragGhostHelper _dragGhost = new();

    // Drag state
    private Point _dragStartPoint;
    private bool _dragInProgress;
    private string? _dragSourceSection;
    private BookmarkItem? _dragItem;
    private const double DragThreshold = 5.0;

    private static readonly SolidColorBrush HighlightBrush = new(Color.FromArgb(0x20, 0x00, 0x78, 0xD4));

    public SidebarControl()
    {
        InitializeComponent();

        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _openTimer.Tick += (s, e) => { _openTimer.Stop(); ShowFloat(); };

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _closeTimer.Tick += (s, e) => { _closeTimer.Stop(); HideFloat(); };
    }

    private SidebarViewModel? ViewModel => DataContext as SidebarViewModel;

    private void ShowFloat()
    {
        FloatPanel.Visibility = Visibility.Visible;
        UpdateEmptyStates();
    }

    private void HideFloat()
    {
        FloatPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmptyStates()
    {
        var vm = ViewModel;
        if (vm == null) return;
        PinnedEmpty.Visibility = vm.PinnedItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TodayEmpty.Visibility = vm.TodayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== Hover open/close ====================

    private void Sidebar_MouseEnter(object sender, MouseEventArgs e)
    {
        _closeTimer.Stop();
        _openTimer.Start();
    }

    private void IconStrip_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragInProgress) return;
        _openTimer.Stop();
        if (FloatPanel.Visibility == Visibility.Visible)
            _closeTimer.Start();
    }

    private void FloatPanel_MouseEnter(object sender, MouseEventArgs e)
    {
        _closeTimer.Stop();
    }

    private void FloatPanel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragInProgress) return;
        _openTimer.Stop();
        _closeTimer.Start();
    }

    // ==================== Item hover ====================

    private void Item_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
    }

    private void Item_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = Brushes.Transparent;
    }

    // ==================== Click ====================

    private void Pinned_Click(object sender, MouseButtonEventArgs e)
    {
        if (_dragInProgress) return;
        if (sender is FrameworkElement fe && fe.DataContext is BookmarkItem item)
        {
            ViewModel?.OnNavigate(item.FullPath);
            HideFloat();
        }
    }

    private void Today_Click(object sender, MouseButtonEventArgs e)
    {
        if (_dragInProgress) return;
        if (sender is FrameworkElement fe && fe.DataContext is BookmarkItem item)
        {
            ViewModel?.OnNavigate(item.FullPath);
            HideFloat();
        }
    }

    private void PinnedDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is BookmarkItem item)
        {
            ViewModel?.RemovePinned(item);
            UpdateEmptyStates();
        }
    }

    private void PinnedClose_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BookmarkItem item)
        {
            ViewModel?.RemovePinned(item);
            UpdateEmptyStates();
        }
        e.Handled = true;
    }

    private void TodayClose_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BookmarkItem item)
        {
            ViewModel?.RemoveToday(item);
            UpdateEmptyStates();
        }
        e.Handled = true;
    }

    // ==================== Drag initiation ====================

    private void Item_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        _dragInProgress = false;
    }

    private void Item_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragInProgress)
            return;

        var pos = e.GetPosition(this);
        var diff = pos - _dragStartPoint;
        if (Math.Abs(diff.X) < DragThreshold && Math.Abs(diff.Y) < DragThreshold)
            return;

        if (sender is not Border border || border.DataContext is not BookmarkItem item)
            return;

        _dragInProgress = true;
        _dragSourceSection = border.Tag as string ?? "";
        _dragItem = item;

        _dragGhost.Show(item.Name, item.Icon);
        border.GiveFeedback += DragGhost_Feedback;

        try
        {
            var data = new DataObject(DataFormats.Text, item.FullPath);
            DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
        }
        finally
        {
            border.GiveFeedback -= DragGhost_Feedback;
            _dragGhost.Close();
            _dragInProgress = false;
            _dragSourceSection = null;
            _dragItem = null;
        }
    }

    private void DragGhost_Feedback(object sender, GiveFeedbackEventArgs e)
    {
        _dragGhost.UpdatePosition();
    }

    // ==================== Drop handling on FloatPanel ====================

    private string? HitTestZone(DragEventArgs e)
    {
        var pinnedPos = e.GetPosition(PinnedDropZone);
        if (pinnedPos.Y >= 0 && pinnedPos.Y <= PinnedDropZone.ActualHeight)
            return "Pinned";

        var todayPos = e.GetPosition(TodayDropZone);
        if (todayPos.Y >= 0 && todayPos.Y <= TodayDropZone.ActualHeight)
            return "Today";

        return null;
    }

    private int GetDropIndex(DragEventArgs e, ListBox listBox)
    {
        var pos = e.GetPosition(listBox);
        for (int i = 0; i < listBox.Items.Count; i++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                var itemPos = container.TranslatePoint(new Point(0, 0), listBox);
                var midY = itemPos.Y + container.ActualHeight / 2;
                if (pos.Y < midY) return i;
            }
        }
        return listBox.Items.Count;
    }

    private void FloatPanel_DragOver(object sender, DragEventArgs e)
    {
        if (_dragSourceSection == null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var zone = HitTestZone(e);

        // Clear highlights
        PinnedDropZone.Background = Brushes.Transparent;
        TodayDropZone.Background = Brushes.Transparent;

        if (zone != null)
        {
            e.Effects = DragDropEffects.Move;
            if (zone != _dragSourceSection)
            {
                if (zone == "Pinned") PinnedDropZone.Background = HighlightBrush;
                else TodayDropZone.Background = HighlightBrush;
            }
        }
        else
        {
            e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void FloatPanel_Drop(object sender, DragEventArgs e)
    {
        PinnedDropZone.Background = Brushes.Transparent;
        TodayDropZone.Background = Brushes.Transparent;

        var zone = HitTestZone(e);
        var vm = ViewModel;
        var item = _dragItem;
        var source = _dragSourceSection;

        if (vm == null || item == null || zone == null)
        {
            e.Handled = true;
            return;
        }

        if (zone != source)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (source == "Today" && zone == "Pinned")
                    vm.PinItem(item);
                else if (source == "Pinned" && zone == "Today")
                    vm.UnpinItem(item);

                UpdateEmptyStates();
            });
        }
        else if (zone == "Pinned" && source == "Pinned")
        {
            var targetIndex = GetDropIndex(e, PinnedList);
            Dispatcher.BeginInvoke(() =>
            {
                vm.MovePinned(item, targetIndex);
            });
        }
        e.Handled = true;
    }

    private void FloatPanel_DragLeaveEvent(object sender, DragEventArgs e)
    {
        PinnedDropZone.Background = Brushes.Transparent;
        TodayDropZone.Background = Brushes.Transparent;
    }
}
