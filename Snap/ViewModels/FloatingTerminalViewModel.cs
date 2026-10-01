using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;
using Snap.Helpers;

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
    private EventHandler? _exitedHandler;
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
        StartShellAsync().SafeFireAndForget("Terminal.StartShell", "ターミナルを起動できません");
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    /// <summary>conhost の PID とその子（pwsh）の PID。</summary>
    private static HashSet<uint> GetProcessFamily(int conhostPid)
    {
        var pids = new HashSet<uint> { (uint)conhostPid };
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return pids;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
            {
                do
                {
                    if (e.th32ParentProcessID == (uint)conhostPid) pids.Add(e.th32ProcessID);
                } while (Process32NextW(snap, ref e));
            }
        }
        finally { CloseHandle(snap); }
        return pids;
    }

    /// <summary>
    /// conhost が作ったコンソールウィンドウ（ConsoleWindowClass）を探す（#22）。
    /// コンソールウィンドウの GetWindowThreadProcessId は conhost ではなく接続しているクライアント
    /// （pwsh）の PID を返すため、conhost とその子の両方の PID で照合する。
    /// PseudoConsoleWindow（Windows Terminal 経由の ConPTY）は埋め込めないので対象にしない。
    /// </summary>
    private static IntPtr FindConsoleWindow(int conhostPid, out string seen)
    {
        var pids = GetProcessFamily(conhostPid);
        IntPtr found = IntPtr.Zero;
        var others = new List<string>();

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (!pids.Contains(windowPid)) return true;
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, 256);
            var cls = sb.ToString();
            if (cls == "ConsoleWindowClass")
            {
                found = hWnd;
                others.Add($"{cls}(pid {windowPid}, matched)");
                return false;
            }
            others.Add($"{cls}(pid {windowPid})");
            return true;
        }, IntPtr.Zero);

        seen = $"pids [{string.Join(",", pids)}] other windows [{string.Join(", ", others)}]";
        return found;
    }

    private async Task StartShellAsync()
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

            // Capture the process this handler belongs to. A previous conhost that exits
            // after a restart must not kill the *current* shell, so only react when the
            // process that fired Exited is still the live _shellProcess.
            var proc = _shellProcess;
            _exitedHandler = (s, e) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(s, _shellProcess) && IsVisible) Close();
                });
            };
            _shellProcess.Exited += _exitedHandler;

            _shellProcess.Start();
            var pid = _shellProcess.Id;

            // conhost の ConsoleWindowClass ウィンドウを探す（100ms 間隔・最大 5 秒・#22）
            ShellWindowHandle = IntPtr.Zero;
            string seen = "";
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(100);

                // Bail if this VM was disposed or a newer StartShell/StopShell replaced
                // the process while we were polling — otherwise we would arm embedding for
                // an already-killed process and leave a dangling handle.
                if (_disposed || !ReferenceEquals(_shellProcess, proc))
                    return;

                var hwnd = FindConsoleWindow(pid, out seen);
                if (hwnd != IntPtr.Zero)
                {
                    ShellWindowHandle = hwnd;
                    Snap.Services.Log.Info("Terminal.StartShell",
                        $"console window {hwnd:X} found after {(i + 1) * 100} ms (conhost {pid}; {seen})");
                    break;
                }
            }

            if (ShellWindowHandle != IntPtr.Zero)
            {
                ShowWindow(ShellWindowHandle, 0); // SW_HIDE
                ShellWindowReady?.Invoke();
            }
            else
            {
                // Leave the shell running as its own window and drop the empty frame.
                Snap.Services.Log.UserError("Terminal.StartShell", "ターミナルを埋め込めません（別ウィンドウで開いています）");
                Snap.Services.Log.Info("Terminal.StartShell", $"no ConsoleWindowClass for conhost {pid}: {seen}");
                IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            // Failed to start
            Snap.Services.Log.UserError("Terminal.StartShell", "ターミナルを起動できません", ex);
        }
    }

    private void StopShell()
    {
        ShellWindowHandle = IntPtr.Zero;
        if (_shellProcess != null)
        {
            // Detach Exited before disposing so a stale handler can't fire against a new shell.
            if (_exitedHandler != null)
                _shellProcess.Exited -= _exitedHandler;
            try
            {
                if (!_shellProcess.HasExited)
                    _shellProcess.Kill(true);
            }
            catch (Exception ex) { Snap.Services.Log.Warn("Terminal.StopShell", "shell kill failed", ex); }
            finally
            {
                _shellProcess.Dispose();
                _shellProcess = null;
                _exitedHandler = null;
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
