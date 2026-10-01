using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Snap.Helpers;
using Snap.Models;
using Snap.Services;
using Snap.ViewModels;
using Snap.Views;

namespace Snap;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new(new WpfDialogService());
    private PerformanceCounter? _cpuCounter;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _usageSaveTimer;

    private AppSettings _settings = new();


    // Low-level keyboard hook, installed only while the embedded terminal is shown (#14): the
    // console window has the keyboard then, so Esc / Ctrl+T / Ctrl+Space never reach WPF.
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

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);

    /// <summary>The window with the keyboard focus on the foreground thread (another process's
    /// console window while the terminal is focused).</summary>
    private static IntPtr GetForegroundFocus()
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(0, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

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

        // Start the icon worker here, on the UI thread (it hooks app shutdown), before the first
        // list load asks for it from the thread pool (#16).
        _ = Snap.Interop.IconWorker.Instance;

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionStr = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "";
        TitleText.Text = $"Snap {versionStr}";

        // Failures that started from a user action are also shown in the status bar (#10).
        Log.UserFacing += OnUserFacingError;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        // Ctrl+T drives the view (the terminal's HwndHost), so it is bound here; the rest are
        // MainWindow.xaml InputBindings on MainViewModel commands (#14). Ctrl+Space is in
        // Window_PreviewKeyDown: the ListView takes Ctrl+Space (select toggle) before a
        // KeyBinding on the window would see it.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => ToggleFloatingTerminalAsync().SafeFireAndForget("MainWindow.Terminal", "ターミナルを開けません")),
            Key.T, ModifierKeys.Control));
        _viewModel.PaneFocusRequested += FocusPane;

        // The low-level hook lives exactly as long as the terminal is shown (#14).
        _viewModel.Terminal.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(FloatingTerminalViewModel.IsVisible)) return;
            if (_viewModel.Terminal.IsVisible) InstallKeyboardHook();
            else
            {
                UninstallKeyboardHook();
                // The console window had the keyboard: give it back to Snap's active list.
                Activate();
                if (_viewModel.ActivePane is { } pane) FocusPane(pane);
            }
        };
        // Safety net: a WH_KEYBOARD_LL hook that outlives its delegate causes system-wide
        // input lag. Closing can be bypassed (Environment.Exit/Shutdown), so also unhook
        // when the dispatcher begins shutting down.
        Dispatcher.ShutdownStarted += (_, _) => UninstallKeyboardHook();
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
            catch (Exception ex) { Log.Warn("MainWindow.Icon", "window icon not set", ex); }
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x0002;

    /// <summary>
    /// WM_DEVICECHANGE (#16): a volume arrived or was removed (USB stick, card, mapped / subst
    /// drive that broadcasts). Only DBT_DEVTYP_VOLUME is handled; MainViewModel rebuilds the
    /// tree's drives and the PC views and moves tabs off a removed drive.
    /// </summary>
    private void OnDeviceChange(IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var evt = wParam.ToInt64();
            if ((evt != DBT_DEVICEARRIVAL && evt != DBT_DEVICEREMOVECOMPLETE) || lParam == IntPtr.Zero) return;
            // DEV_BROADCAST_HDR { dbch_size, dbch_devicetype, dbch_reserved } + DEV_BROADCAST_VOLUME { dbcv_unitmask, dbcv_flags }
            if (Marshal.ReadInt32(lParam, 4) != DBT_DEVTYP_VOLUME) return;
            var mask = Marshal.ReadInt32(lParam, 12);
            var letters = Enumerable.Range(0, 26).Where(i => (mask & (1 << i)) != 0).Select(i => (char)('A' + i)).ToList();
            var removed = evt == DBT_DEVICEREMOVECOMPLETE;
            Log.Info("MainWindow.DeviceChange", $"{(removed ? "removed" : "arrived")}: {string.Join(",", letters)}");
            _viewModel.OnVolumesChanged(removed ? letters : []);
        }
        catch (Exception ex) { Log.Warn("MainWindow.DeviceChange", "WM_DEVICECHANGE not handled", ex); }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            OnDeviceChange(wParam, lParam);
            return IntPtr.Zero;
        }
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
        // async void event handler: nothing may escape to the dispatcher (#13).
        try
        {
            await OnLoadedAsync();
        }
        catch (Exception ex)
        {
            Log.UserError("MainWindow.Loaded", "起動処理の一部に失敗しました", ex);
        }
    }

    private async Task OnLoadedAsync()
    {
        _settings = SettingsStore.Load();
        SettingsStore.Capture = CaptureSettings;
        RestoreWindowState(_settings);

        // ブックマーク復元（初期化の await より前に。途中で閉じても空で上書きしない）
        _viewModel.FolderTree.LoadBookmarks(_settings.Bookmarks);

        await _viewModel.InitializeAsync(_settings);

        // ブックマーク追加のルーティドイベントをキャッチ
        AddHandler(FilePaneControl.AddBookmarkRequestedEvent, new RoutedEventHandler(OnAddBookmarkRequested));
        // Views ask MainViewModel for cross-pane work through routed events (#13).
        AddHandler(TabPaneControl.TabMoveRequestedEvent, new RoutedEventHandler(OnTabMoveRequested));
        AddHandler(FilePaneControl.OpenInNewTabRequestedEvent, new RoutedEventHandler(OnOpenInNewTabRequested));

        InitStatusTimer();

        // Periodic usage save (every 30 seconds)
        _usageSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _usageSaveTimer.Tick += (s, ev) => _viewModel.UsageTracker.Save();
        _usageSaveTimer.Start();

        // Warm up shell extension DLLs on the shell-menu worker once the UI is idle (#5).
        // Speeds up the first right-click; never shows UI, all errors swallowed.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                _ = Snap.Interop.ShellMenuWorker.Instance.WarmUpAsync().ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Log.Warn("MainWindow.WarmUp", "shell menu warm-up failed", t.Exception);
                });
            }
            catch (Exception ex) { Log.Warn("MainWindow.WarmUp", "shell menu warm-up not started", ex); }
        });

        // Layout changes go to settings.json via the debounced store (#12).
        LocationChanged += (_, _) => SettingsStore.MarkDirty();
        SizeChanged += (_, _) => SettingsStore.MarkDirty();
        StateChanged += (_, _) => SettingsStore.MarkDirty();
        AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) => SettingsStore.MarkDirty()),
            handledEventsToo: true);

        // Everything is restored: from here on changes are saved.
        SettingsStore.Ready = true;

        // Tab initialization overwrote the status bar; show the load failure again.
        if (SettingsStore.LoadError is { } loadError)
            _viewModel.ShowStatus(loadError);
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
        if (_keyboardHookId != IntPtr.Zero) return;
        _keyboardHookProc ??= LowLevelKeyboardCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc,
            GetModuleHandle(curModule.ModuleName), 0);
        if (_keyboardHookId == IntPtr.Zero)
            Log.Warn("MainWindow.KeyboardHook", $"SetWindowsHookEx failed ({Marshal.GetLastWin32Error()})");
        else
            Log.Info("MainWindow.KeyboardHook", "installed (terminal shown)");
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
            Log.Info("MainWindow.KeyboardHook", "removed");
        }
    }

    private IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_KEYDOWN)
        {
            // Only while the terminal's console window has the keyboard. It stays a top-level
            // window of conhost (GA_ROOT is itself), so compare with its handle directly.
            // When Snap itself has the focus, WPF's InputBindings / PreviewKeyDown handle these
            // keys (and a TextBox keeps Ctrl+Space).
            var termWnd = _viewModel.Terminal.ShellWindowHandle;
            var fgWnd = GetForegroundWindow();
            if (termWnd == IntPtr.Zero
                || (fgWnd != termWnd && GetAncestor(fgWnd, 2) != termWnd && GetForegroundFocus() != termWnd))
                return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

            int vkCode = Marshal.ReadInt32(lParam);
            bool ctrl = IsKeyDown(VK_CONTROL);

            if (vkCode == VK_ESCAPE && _viewModel.Terminal.IsVisible)
            {
                Dispatcher.BeginInvoke(() => _viewModel.Terminal.Close());
                return (IntPtr)1;
            }
            if (vkCode == VK_T && ctrl)
            {
                Dispatcher.BeginInvoke(() =>
                    ToggleFloatingTerminalAsync().SafeFireAndForget("MainWindow.Terminal", "ターミナルを開けません"));
                return (IntPtr)1;
            }
            if (vkCode == VK_SPACE && ctrl)
            {
                // The console is the foreground window: bring Snap forward so the palette gets the keys.
                Dispatcher.BeginInvoke(() => { Activate(); ToggleCommandPalette(); });
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            UninstallKeyboardHook();
            _viewModel.Terminal.Dispose();
            _viewModel.UsageTracker.Save();
            SettingsStore.FlushNow();
        }
        catch (Exception ex)
        {
            // Never crash on close
            Log.Error("MainWindow.Closing", "shutdown cleanup failed", ex);
        }
    }

    /// <summary>Builds the settings snapshot saved by <see cref="SettingsStore"/>.</summary>
    private AppSettings CaptureSettings()
    {
        var settings = new AppSettings();

        // Window state — capture RestoreBounds when maximized or minimized
        // (a minimized window reports Left/Top = -32000; saves now also happen while minimized).
        var isMax = WindowState == WindowState.Maximized;
        var useRestore = WindowState != WindowState.Normal && !RestoreBounds.IsEmpty;
        settings.Window = new WindowSettings
        {
            Width = useRestore ? RestoreBounds.Width : Width,
            Height = useRestore ? RestoreBounds.Height : Height,
            Left = useRestore ? RestoreBounds.Left : Left,
            Top = useRestore ? RestoreBounds.Top : Top,
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
        settings.Bookmarks = _viewModel.FolderTree.GetBookmarks();

        // Sidebar state
        settings.TodayFolders = _viewModel.Sidebar.GetTodayPaths();

        return settings;
    }

    private void InitStatusTimer()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue();
        }
        catch (Exception ex)
        {
            Log.Warn("MainWindow.CpuCounter", "CPU performance counter unavailable", ex);
            _cpuCounter = null;
        }

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (s, e) => UpdateSystemInfo();
        _statusTimer.Start();
        UpdateSystemInfo();
    }

    // The status tick runs every second; log each failing source only once per session.
    private readonly HashSet<string> _sysInfoWarned = new();

    private void WarnSysInfoOnce(string source, Exception ex)
    {
        if (_sysInfoWarned.Add(source))
            Log.Warn("MainWindow.SystemInfo", $"{source} status unavailable (further failures not logged)", ex);
    }

    private void UpdateSystemInfo()
    {
        // CPU
        try { CpuText.Text = $"CPU {_cpuCounter?.NextValue() ?? 0:F0}%"; }
        catch (Exception ex) { CpuText.Text = "CPU --"; WarnSysInfoOnce("cpu", ex); }

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
        catch (Exception ex) { MemText.Text = "MEM --"; WarnSysInfoOnce("mem", ex); }

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
        catch (Exception ex) { BatteryText.Text = ""; WarnSysInfoOnce("battery", ex); }

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

    // Active pane: MainViewModel owns it; the view only reports the click / keyboard focus.
    private void Pane_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TabPaneControl pane && pane.DataContext is TabPaneViewModel vm)
            _viewModel.ActivePane = vm;
    }

    private void Pane_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TabPaneControl pane && pane.DataContext is TabPaneViewModel vm)
            _viewModel.ActivePane = vm;
    }

    /// <summary>Ctrl+1..4: MainViewModel made the pane active; put the keyboard in its list.</summary>
    private void FocusPane(TabPaneViewModel pane)
    {
        TabPaneControl? view =
            pane == _viewModel.TopLeftPane ? TopLeftPaneView :
            pane == _viewModel.TopRightPane ? TopRightPaneView :
            pane == _viewModel.BottomLeftPane ? BottomLeftPaneView :
            pane == _viewModel.BottomRightPane ? BottomRightPaneView : null;
        view?.FocusFileList();
    }

    private void OnOpenInNewTabRequested(object sender, RoutedEventArgs e)
    {
        if (e is OpenInNewTabRequestedEventArgs args)
            _viewModel.OpenInNewTab(args.From, args.Path, args.Select)
                .SafeFireAndForget("MainWindow.OpenInNewTab", $"新しいタブで開けません（{args.Path}）");
    }

    private void OnTabMoveRequested(object sender, RoutedEventArgs e)
    {
        if (e is TabMoveRequestedEventArgs args)
            args.Moved = _viewModel.MoveTab(args.Tab, args.Target);
    }

    private void OnUserFacingError(string message)
    {
        void Show() => _viewModel.ShowStatus(message);
        if (Dispatcher.CheckAccess()) Show();
        else Dispatcher.BeginInvoke(Show);
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

        // Ctrl+Space — command palette. Handled on the tunnel so the ListView does not take it;
        // a focused text box keeps it (IME etc.), except the palette's own input (#14).
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control && CanTogglePaletteByKey())
        {
            ToggleCommandPalette();
            e.Handled = true;
            return;
        }

        // Ctrl+T — KeyBinding added in the constructor. While the terminal's console window has
        // the focus, the low-level hook covers Esc / Ctrl+T / Ctrl+Space instead.

        // Escape — close floating panels
        if (e.Key == Key.Escape)
        {
            if (_viewModel.CommandPalette.IsVisible)
            {
                _viewModel.CommandPalette.Close();
                e.Handled = true;
                return;
            }
            if (_viewModel.Terminal.IsVisible)
            {
                _viewModel.Terminal.Close();
                e.Handled = true;
                return;
            }
        }
    }

    // ==================== Command Palette ====================

    /// <summary>Ctrl+Space passes through to a focused text box (IME and other users of the key),
    /// except the palette's own input, where it closes the palette.</summary>
    private bool CanTogglePaletteByKey() =>
        Keyboard.FocusedElement is not TextBox box || box == CommandPaletteInput;

    private void ToggleCommandPalette()
    {
        _viewModel.ToggleCommandPalette();

        if (_viewModel.CommandPalette.IsVisible)
        {
            CommandPaletteInput.Focus();
            CommandPaletteInput.SelectAll();
        }
    }

    private async void CommandPaletteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var cp = _viewModel.CommandPalette;

        try
        {
            switch (e.Key)
            {
                case Key.Escape:
                    cp.Close();
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
                    e.Handled = true;
                    break;
            }
        }
        catch (Exception ex) { Log.UserError("CommandPalette.Execute", "コマンドを実行できません", ex); }
    }

    private async void CommandPaletteResults_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var cp = _viewModel.CommandPalette;
        try
        {
            if (cp.SelectedItem != null)
                await cp.ExecuteSelected();
        }
        catch (Exception ex) { Log.UserError("CommandPalette.Execute", "コマンドを実行できません", ex); }
    }

    // ==================== Floating Terminal ====================

    private async Task ToggleFloatingTerminalAsync()
    {
        var term = _viewModel.Terminal;

        if (term.IsVisible)
        {
            term.Close();
            return;
        }

        // Sync current directory
        var activeTab = _viewModel.ActiveTab;
        if (activeTab != null)
            term.CurrentDirectory = activeTab.CurrentPath;

        // Make visible FIRST (the border is bound to IsVisible) so HwndHost gets initialized via layout
        term.IsVisible = true;

        // Wait for layout to complete (HwndHost.BuildWindowCore)
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        // Now start shell — host window handle is ready.
        // Unsubscribe first so a previous open that never raised ShellWindowReady (e.g. a
        // failed shell spawn) can't leave a duplicate subscription that fires EmbedTerminalAsync
        // multiple times on the next successful open.
        term.ShellWindowReady -= OnShellWindowReady;
        term.ShellWindowReady += OnShellWindowReady;
        term.Open();
    }

    private void OnShellWindowReady()
    {
        var term = _viewModel.Terminal;
        term.ShellWindowReady -= OnShellWindowReady;
        EmbedTerminalAsync(term).SafeFireAndForget("MainWindow.Terminal", "ターミナルを埋め込めません");
    }

    private async Task EmbedTerminalAsync(FloatingTerminalViewModel term)
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
    }
}
