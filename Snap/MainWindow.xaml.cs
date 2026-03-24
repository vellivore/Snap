using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Snap.Models;
using Snap.Services;
using Snap.ViewModels;
using Snap.Views;

namespace Snap;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private PerformanceCounter? _cpuCounter;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _usageSaveTimer;

    private AppSettings _settings = new();


    // Low-level keyboard hook for Escape when conhost has focus
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _keyboardHookProc;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_T = 0x54;
    private const int VK_SPACE = 0x20;
    private const int VK_CONTROL = 0x11;

    // Ctrl+Space+T for terminal (no longer needs long press)

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        InstallKeyboardHook();
        SourceInitialized += OnSourceInitialized;

        // Set window icon via Win32 API for proper taskbar display
        SourceInitialized += (s, e) =>
        {
            try
            {
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    // Load at 256x256 for big icon - Windows downscales beautifully
                    var bigIcon = LoadImage(IntPtr.Zero, iconPath, 1/*IMAGE_ICON*/, 256, 256, 0x10/*LR_LOADFROMFILE*/);
                    if (bigIcon != IntPtr.Zero)
                        SendMessage(hwnd, 0x0080, (IntPtr)1, bigIcon);
                    // Small icon at system size
                    var smallSize = GetSystemMetrics(11); // SM_CXSMICON
                    var smallIcon = LoadImage(IntPtr.Zero, iconPath, 1, smallSize, smallSize, 0x10);
                    if (smallIcon != IntPtr.Zero)
                        SendMessage(hwnd, 0x0080, (IntPtr)0, smallIcon);
                }
            }
            catch { }
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024)
        {
            var monitor = MonitorFromWindow(hwnd, 2);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var work = monitorInfo.rcWork;
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMaxPosition.X = work.Left;
                    mmi.ptMaxPosition.Y = work.Top;
                    mmi.ptMaxSize.X = work.Right - work.Left;
                    mmi.ptMaxSize.Y = work.Bottom - work.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                }
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = SettingsService.Load();
        RestoreWindowState(_settings);
        await _viewModel.InitializeAsync(_settings);

        // ブックマーク復元
        _viewModel.FolderTree.LoadBookmarks(_settings.Bookmarks);

        // ブックマーク追加のルーティドイベントをキャッチ
        AddHandler(FilePaneControl.AddBookmarkRequestedEvent, new RoutedEventHandler(OnAddBookmarkRequested));

        InitStatusTimer();

        // Periodic usage save (every 30 seconds)
        _usageSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _usageSaveTimer.Tick += (s, ev) => _viewModel.UsageTracker.Save();
        _usageSaveTimer.Start();

        // Wire up command palette actions
        WireCommandPalette();

    }

    private void RestoreWindowState(AppSettings settings)
    {
        var w = settings.Window;

        // Restore size
        Width = w.Width > 0 ? w.Width : 1400;
        Height = w.Height > 0 ? w.Height : 800;

        // Restore position (only if within screen bounds)
        if (!double.IsNaN(w.Left) && !double.IsNaN(w.Top))
        {
            Left = w.Left;
            Top = w.Top;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        // Restore maximized state
        if (w.IsMaximized)
        {
            WindowState = WindowState.Maximized;
            MaxRestoreButton.Content = "❐";
        }

        // Restore tree width
        if (settings.TreeWidth > 0)
        {
            TreeColumn.Width = new GridLength(settings.TreeWidth, GridUnitType.Pixel);
        }

        // Restore pane split ratios
        if (settings.HorizontalSplit is { Length: 2 })
        {
            TopPaneRow.Height = new GridLength(Math.Max(settings.HorizontalSplit[0], 0.1), GridUnitType.Star);
            BottomPaneRow.Height = new GridLength(Math.Max(settings.HorizontalSplit[1], 0.1), GridUnitType.Star);
        }

        if (settings.VerticalSplit is { Length: 2 })
        {
            LeftPaneColumn.Width = new GridLength(Math.Max(settings.VerticalSplit[0], 0.1), GridUnitType.Star);
            RightPaneColumn.Width = new GridLength(Math.Max(settings.VerticalSplit[1], 0.1), GridUnitType.Star);
        }
    }

    private void InstallKeyboardHook()
    {
        _keyboardHookProc = LowLevelKeyboardCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc,
            GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_KEYDOWN)
        {
            // Only handle when Snap is the foreground window
            var fgWnd = GetForegroundWindow();
            var myWnd = new WindowInteropHelper(this).Handle;
            if (fgWnd != myWnd && !IsChildOfWindow(fgWnd, myWnd))
                return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

            int vkCode = Marshal.ReadInt32(lParam);
            bool ctrl = IsKeyDown(VK_CONTROL);

            if (vkCode == VK_ESCAPE && _viewModel.Terminal.IsVisible)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _viewModel.Terminal.Close();
                    FloatingTerminalBorder.Visibility = Visibility.Collapsed;
                });
                return (IntPtr)1;
            }
            if (vkCode == VK_T && ctrl)
            {
                Dispatcher.BeginInvoke(() => ToggleFloatingTerminal());
                return (IntPtr)1;
            }
            if (vkCode == VK_SPACE && ctrl)
            {
                Dispatcher.BeginInvoke(() => ToggleCommandPalette());
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private bool IsChildOfWindow(IntPtr hwnd, IntPtr parentHwnd)
    {
        // GA_ROOT = 2: get the root owner window
        var root = GetAncestor(hwnd, 2);
        return root == parentHwnd;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            if (_keyboardHookId != IntPtr.Zero)
                UnhookWindowsHookEx(_keyboardHookId);
            _viewModel.Terminal.Dispose();
            SaveSettings();
        }
        catch
        {
            // Never crash on close
        }
    }

    private void SaveSettings()
    {
        _viewModel.UsageTracker.Save();
        var settings = new AppSettings();

        // Window state — capture RestoreBounds when maximized
        var isMax = WindowState == WindowState.Maximized;
        settings.Window = new WindowSettings
        {
            Width = isMax ? RestoreBounds.Width : Width,
            Height = isMax ? RestoreBounds.Height : Height,
            Left = isMax ? RestoreBounds.Left : Left,
            Top = isMax ? RestoreBounds.Top : Top,
            IsMaximized = isMax,
        };

        // Tree width
        settings.TreeWidth = TreeColumn.ActualWidth;

        // Split ratios
        settings.HorizontalSplit =
        [
            TopPaneRow.Height.Value,
            BottomPaneRow.Height.Value,
        ];
        settings.VerticalSplit =
        [
            LeftPaneColumn.Width.Value,
            RightPaneColumn.Width.Value,
        ];

        // Pane tab state
        settings.Panes = _viewModel.GetPanesState();

        // Bookmarks
        settings.Bookmarks = _viewModel.FolderTree.GetBookmarkPaths();

        // Sidebar state
        settings.TodayFolders = _viewModel.Sidebar.GetTodayPaths();

        SettingsService.Save(settings);
    }

    private void InitStatusTimer()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue();
        }
        catch { _cpuCounter = null; }

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (s, e) => UpdateSystemInfo();
        _statusTimer.Start();
        UpdateSystemInfo();
    }

    private void UpdateSystemInfo()
    {
        // CPU
        try { CpuText.Text = $"CPU {_cpuCounter?.NextValue() ?? 0:F0}%"; }
        catch { CpuText.Text = "CPU --"; }

        // Memory
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                var used = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024 * 1024);
                var total = mem.ullTotalPhys / (1024.0 * 1024 * 1024);
                MemText.Text = $"MEM {used:F1}/{total:F0}GB";
            }
        }
        catch { MemText.Text = "MEM --"; }

        // Battery
        try
        {
            var sps = new SYSTEM_POWER_STATUS();
            if (GetSystemPowerStatus(ref sps))
            {
                if (sps.BatteryFlag == 128) // no battery
                    BatteryText.Text = "AC";
                else
                {
                    var icon = sps.ACLineStatus == 1 ? "⚡" : "";
                    BatteryText.Text = $"{icon}BAT {sps.BatteryLifePercent}%";
                }
            }
        }
        catch { BatteryText.Text = ""; }

        // Clock
        ClockText.Text = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
    }

    // Title bar
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else if (WindowState == WindowState.Maximized)
        {
            var mousePos = PointToScreen(e.GetPosition(this));
            var restoreWidth = RestoreBounds.Width;
            var restoreHeight = RestoreBounds.Height;

            WindowState = WindowState.Normal;
            MaxRestoreButton.Content = "☐";

            Left = mousePos.X - restoreWidth / 2;
            Top = mousePos.Y - 16;
            Width = restoreWidth;
            Height = restoreHeight;

            DragMove();
        }
        else
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaxRestoreButton.Content = "☐";
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaxRestoreButton.Content = "❐";
        }
    }

    private void OnAddBookmarkRequested(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FilePaneControl fpc && !string.IsNullOrEmpty(fpc.PendingBookmarkPath))
        {
            _viewModel.FolderTree.AddBookmark(fpc.PendingBookmarkPath);
            fpc.PendingBookmarkPath = null;
        }
    }

    // Active pane tracking
    private FilePaneViewModel? _trackedTab;

    private void Pane_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TabPaneControl pane && pane.DataContext is TabPaneViewModel vm)
        {
            _viewModel.ActivePane = vm;
            TrackActiveTab();
        }
    }

    private void TrackActiveTab()
    {
        if (_trackedTab != null)
            _trackedTab.PropertyChanged -= OnTrackedTabChanged;

        _trackedTab = _viewModel.ActivePane?.SelectedTab;

        if (_trackedTab != null)
        {
            _trackedTab.PropertyChanged += OnTrackedTabChanged;
            StatusBarText.Text = _trackedTab.StatusMessage;
        }
    }

    private void OnTrackedTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePaneViewModel.StatusMessage) && sender is FilePaneViewModel tab)
            StatusBarText.Text = tab.StatusMessage;
    }

    // ==================== Keyboard Shortcuts ====================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+F — open command palette (search)
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleCommandPalette();
            e.Handled = true;
            return;
        }

        // Ctrl+Space — command palette toggle is handled by low-level keyboard hook

        // Ctrl+T — terminal toggle is handled by low-level keyboard hook

        // Escape — close floating panels
        if (e.Key == Key.Escape)
        {
            if (_viewModel.CommandPalette.IsVisible)
            {
                _viewModel.CommandPalette.Close();
                CommandPaletteBorder.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            if (_viewModel.Terminal.IsVisible)
            {
                _viewModel.Terminal.Close();
                FloatingTerminalBorder.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
    }

    // ==================== Command Palette ====================

    private void WireCommandPalette()
    {
        var cp = _viewModel.CommandPalette;

        cp.NavigateAction = async (path) =>
        {
            var pane = _viewModel.ActivePane ?? _viewModel.TopLeftPane;
            if (pane.SelectedTab != null)
                await pane.SelectedTab.NavigateToAsync(path);
        };

        cp.AddTabAction = async () =>
        {
            var pane = _viewModel.ActivePane ?? _viewModel.TopLeftPane;
            await pane.AddTab();
        };

        cp.CloseTabAction = () =>
        {
            var pane = _viewModel.ActivePane ?? _viewModel.TopLeftPane;
            if (pane.SelectedTab != null)
                pane.CloseTab(pane.SelectedTab);
        };

        cp.RefreshAction = async () =>
        {
            var pane = _viewModel.ActivePane ?? _viewModel.TopLeftPane;
            if (pane.SelectedTab != null)
                await pane.SelectedTab.Refresh();
        };

        cp.OpenSettingsAction = () =>
        {
            var settingsPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Snap", "settings.json");
            try
            {
                Process.Start(new ProcessStartInfo(settingsPath) { UseShellExecute = true });
            }
            catch { }
        };

        cp.OpenTerminalAction = () =>
        {
            var dir = _viewModel.ActivePane?.SelectedTab?.CurrentPath ?? @"C:\";
            try
            {
                Process.Start(new ProcessStartInfo("pwsh.exe")
                {
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                });
            }
            catch { }
        };
    }

    private void ToggleCommandPalette()
    {
        var cp = _viewModel.CommandPalette;

        // Sync current directory
        var activeTab = _viewModel.ActivePane?.SelectedTab;
        if (activeTab != null)
            cp.CurrentDirectory = activeTab.CurrentPath;

        cp.Toggle();
        CommandPaletteBorder.Visibility = cp.IsVisible ? Visibility.Visible : Visibility.Collapsed;

        if (cp.IsVisible)
        {
            CommandPaletteInput.Focus();
            CommandPaletteInput.SelectAll();
        }
    }

    private async void CommandPaletteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var cp = _viewModel.CommandPalette;

        switch (e.Key)
        {
            case Key.Escape:
                cp.Close();
                CommandPaletteBorder.Visibility = Visibility.Collapsed;
                e.Handled = true;
                break;
            case Key.Down:
                cp.SelectNext();
                if (cp.SelectedItem != null)
                    CommandPaletteResults.ScrollIntoView(cp.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                cp.SelectPrevious();
                if (cp.SelectedItem != null)
                    CommandPaletteResults.ScrollIntoView(cp.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                await cp.ExecuteSelected();
                CommandPaletteBorder.Visibility = Visibility.Collapsed;
                e.Handled = true;
                break;
        }
    }

    private async void CommandPaletteResults_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var cp = _viewModel.CommandPalette;
        if (cp.SelectedItem != null)
        {
            await cp.ExecuteSelected();
            CommandPaletteBorder.Visibility = Visibility.Collapsed;
        }
    }

    // ==================== Floating Terminal ====================

    private async void ToggleFloatingTerminal()
    {
        var term = _viewModel.Terminal;

        if (term.IsVisible)
        {
            term.Close();
            FloatingTerminalBorder.Visibility = Visibility.Collapsed;
            return;
        }

        // Sync current directory
        var activeTab = _viewModel.ActivePane?.SelectedTab;
        if (activeTab != null)
            term.CurrentDirectory = activeTab.CurrentPath;

        // Make visible FIRST so HwndHost gets initialized via layout
        FloatingTerminalBorder.Visibility = Visibility.Visible;

        // Wait for layout to complete (HwndHost.BuildWindowCore)
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        // Now start shell — host window handle is ready
        term.ShellWindowReady += OnShellWindowReady;
        term.Open();
    }

    private void OnShellWindowReady()
    {
        var term = _viewModel.Terminal;
        term.ShellWindowReady -= OnShellWindowReady;
        EmbedTerminalAsync(term);
    }

    private async void EmbedTerminalAsync(FloatingTerminalViewModel term)
    {
        // HwndHost の BuildWindowCore 完了をポーリングで待つ（最大2秒）
        IntPtr hostHwnd = IntPtr.Zero;
        for (int i = 0; i < 20; i++)
        {
            hostHwnd = TerminalHost.HostWindowHandle;
            if (hostHwnd != IntPtr.Zero) break;
            await Task.Delay(100);
        }

        if (hostHwnd == IntPtr.Zero) return;

        term.EmbedInto(hostHwnd);

        var w = (int)TerminalHost.ActualWidth;
        var h = (int)TerminalHost.ActualHeight;
        if (w > 0 && h > 0)
            term.ResizeToHost(w, h);

        term.FocusTerminal();
    }

    private void TerminalHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var term = _viewModel.Terminal;
        if (term.IsVisible && term.ShellWindowHandle != IntPtr.Zero)
        {
            term.ResizeToHost((int)e.NewSize.Width, (int)e.NewSize.Height);
        }
    }

    private void TerminalClose_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Terminal.Close();
        FloatingTerminalBorder.Visibility = Visibility.Collapsed;
    }
}
