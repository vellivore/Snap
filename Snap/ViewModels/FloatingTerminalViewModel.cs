using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;

namespace Snap.ViewModels;

/// <summary>
/// pwsh.exe のコンソールウィンドウを WPF 内に埋め込むフローティングターミナル。
/// conhost.exe 経由で起動し（Windows Terminal バイパス）、
/// SetParent Win32 API でコンソールウィンドウを WPF のホストパネルに子ウィンドウ化する。
/// </summary>
public partial class FloatingTerminalViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private bool _isVisible;

    private Process? _shellProcess;
    private bool _disposed;

    // Unused but kept for compatibility (XAML bindings)
    [ObservableProperty]
    private string _outputText = string.Empty;
    [ObservableProperty]
    private string _inputText = string.Empty;
    [ObservableProperty]
    private string _promptText = ">";

    public string CurrentDirectory { get; set; } = @"C:\";

    /// <summary>pwsh プロセスのメインウィンドウハンドル</summary>
    public IntPtr ShellWindowHandle { get; private set; }

    /// <summary>シェルウィンドウが準備できた時に発火</summary>
    public event Action? ShellWindowReady;

    #region Win32 API

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    #endregion

    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_SYSMENU = 0x00080000;

    public void Open()
    {
        IsVisible = true;
        StartShell();
    }

    public void Close()
    {
        IsVisible = false;
        StopShell();
    }

    public void FocusTerminal()
    {
        if (ShellWindowHandle != IntPtr.Zero)
            SetForegroundWindow(ShellWindowHandle);
    }

    public void ResizeToHost(int width, int height)
    {
        if (ShellWindowHandle != IntPtr.Zero)
            MoveWindow(ShellWindowHandle, 0, 0, width, height, true);
    }

    public void EmbedInto(IntPtr hostHandle)
    {
        if (ShellWindowHandle == IntPtr.Zero) return;

        // Remove window chrome and WS_POPUP (mutually exclusive with WS_CHILD)
        var style = GetWindowLong(ShellWindowHandle, GWL_STYLE);
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLong(ShellWindowHandle, GWL_STYLE, style);

        // Reparent into host
        SetParent(ShellWindowHandle, hostHandle);
        ShowWindow(ShellWindowHandle, 1); // SW_SHOWNORMAL
    }

    /// <summary>
    /// プロセスIDに関連するウィンドウを EnumWindows で探す。
    /// Windows Terminal がデフォルトターミナルの場合、MainWindowHandle では
    /// 実際のコンソールウィンドウを取得できないため、この方式を使う。
    /// ConsoleWindowClass を優先し、なければ最初の可視ウィンドウを返す。
    /// </summary>
    private IntPtr FindWindowByProcessTree(int pid)
    {
        IntPtr found = IntPtr.Zero;
        var candidates = new List<(IntPtr hwnd, string className)>();

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == (uint)pid && IsWindowVisible(hWnd))
            {
                var sb = new StringBuilder(256);
                GetClassName(hWnd, sb, 256);
                candidates.Add((hWnd, sb.ToString()));
            }
            return true;
        }, IntPtr.Zero);

        // ConsoleWindowClass を優先
        foreach (var (hwnd, cls) in candidates)
        {
            if (cls == "ConsoleWindowClass")
            {
                found = hwnd;
                break;
            }
        }

        // なければ最初の可視ウィンドウ
        if (found == IntPtr.Zero && candidates.Count > 0)
            found = candidates[0].hwnd;

        return found;
    }

    private async void StartShell()
    {
        StopShell();

        var workDir = Directory.Exists(CurrentDirectory) ? CurrentDirectory : @"C:\";

        try
        {
            // conhost.exe 経由で起動し、Windows Terminal をバイパスする
            _shellProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "conhost.exe",
                    Arguments = "pwsh.exe -NoLogo",
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                },
                EnableRaisingEvents = true,
            };

            _shellProcess.Exited += (s, e) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (IsVisible) Close();
                });
            };

            _shellProcess.Start();
            var pid = _shellProcess.Id;

            // conhost の ConsoleWindowClass ウィンドウを EnumWindows で探す
            ShellWindowHandle = IntPtr.Zero;
            for (int i = 0; i < 50; i++) // max 5 seconds
            {
                await Task.Delay(100);

                var hwnd = FindWindowByProcessTree(pid);
                if (hwnd != IntPtr.Zero)
                {
                    ShellWindowHandle = hwnd;
                    break;
                }
            }

            if (ShellWindowHandle != IntPtr.Zero)
            {
                ShowWindow(ShellWindowHandle, 0); // SW_HIDE
                ShellWindowReady?.Invoke();
            }
        }
        catch
        {
            // Failed to start
        }
    }

    private void StopShell()
    {
        ShellWindowHandle = IntPtr.Zero;
        if (_shellProcess != null)
        {
            try
            {
                if (!_shellProcess.HasExited)
                    _shellProcess.Kill(true);
            }
            catch { }
            finally
            {
                _shellProcess.Dispose();
                _shellProcess = null;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            StopShell();
        }
    }
}
