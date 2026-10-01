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

    /// <summary>新しいシェルを起動するときの作業フォルダ（開く直前にアクティブペインのフォルダが入る）。</summary>
    public string CurrentDirectory { get; set; } = @"C:\";

    /// <summary>開いたときにアクティブペインのフォルダへ自動で cd する（settings.json の Terminal.FollowActivePane・#18）。</summary>
    public bool FollowActivePane { get; set; }

    /// <summary>pwsh プロセスのメインウィンドウハンドル</summary>
    public IntPtr ShellWindowHandle { get; private set; }

    /// <summary>コンソール窓を最後に SetParent したホストのハンドル（ホストが作り直されたら埋め直す）。</summary>
    public IntPtr EmbeddedHost { get; private set; }

    /// <summary>シェルが生きていて、コンソール窓が見つかっている。閉じた（非表示の）ままでも true（#18）。</summary>
    public bool IsShellRunning =>
        ShellWindowHandle != IntPtr.Zero && _shellProcess is { } p && !HasExitedSafe(p);

    /// <summary>
    /// 直近の起動でコンソール窓を埋め込めず、シェルを別ウィンドウのまま残した（#24）。
    /// このとき枠を閉じても MainWindow は Activate しない（別窓が Snap の後ろに回るため）。
    /// </summary>
    public bool LeftAsSeparateWindow { get; private set; }

    // The console's client process (pwsh): the PID the console window reports, used to attach.
    private uint _consoleClientPid;

    private static bool HasExitedSafe(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

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
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct KEY_INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int bKeyDown;
        [FieldOffset(8)] public ushort wRepeatCount;
        [FieldOffset(10)] public ushort wVirtualKeyCode;
        [FieldOffset(12)] public ushort wVirtualScanCode;
        [FieldOffset(14)] public char UnicodeChar;
        [FieldOffset(16)] public uint dwControlKeyState;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInputW(IntPtr hConsoleInput, KEY_INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

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
    private const ushort KEY_EVENT = 0x0001;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_ESCAPE = 0x1B;
    /// <summary>WriteToConsole の文字列中で Esc キーを表す文字（PSReadLine では入力行の取り消し）。</summary>
    private const char EscapeKey = '\u001b';
    private const uint GENERIC_READ_WRITE = 0xC0000000;
    private const uint FILE_SHARE_READ_WRITE = 0x00000003;
    private const uint OPEN_EXISTING = 3;
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;

    /// <summary>新しいシェルを起動する（既存のシェルは終了させる）。</summary>
    public void Open()
    {
        IsVisible = true;
        StartShellAsync().SafeFireAndForget("Terminal.StartShell", "ターミナルを起動できません");
    }

    /// <summary>枠を隠すだけ。シェルは生かしておき、次に開いたとき同じセッションを出す（#18）。</summary>
    public void Close()
    {
        IsVisible = false;
    }

    /// <summary>シェルを終了させる（パレットの /terminal kill とアプリ終了時だけ・#18）。</summary>
    public void Kill()
    {
        StopShell();
    }

    /// <summary>
    /// シェルを <paramref name="path"/> へ移す（#18）。<c>Set-Location -LiteralPath '...'</c> と Enter を
    /// コンソールの入力バッファへ書く（pwsh の標準入力は持たないため）。
    /// 先に Esc を 1 つ送り、PSReadLine の入力途中の行を消してから打つ（#24）。
    /// シェル内で手で cd した場合を知る手段がないので、同じフォルダかどうかは比べずに常に送る（#24）。
    /// </summary>
    public bool ChangeDirectory(string path)
    {
        if (!IsShellRunning || string.IsNullOrEmpty(path)) return false;

        var command = "Set-Location -LiteralPath '" + path.Replace("'", "''") + "'";
        if (!WriteToConsole(EscapeKey + command + "\r"))
        {
            Snap.Services.Log.UserError("Terminal.ChangeDirectory", "ターミナルへ cd を送れません");
            return false;
        }
        Snap.Services.Log.Info("Terminal.ChangeDirectory", path);
        return true;
    }

    /// <summary>
    /// Types <paramref name="text"/> into the shell's console as key events (CR = Enter,
    /// <see cref="EscapeKey"/> = Esc).
    /// Posting WM_CHAR / WM_KEYDOWN to the console window does not reach the shell, so Snap
    /// (a GUI process without a console of its own) attaches to the console for the duration
    /// of one WriteConsoleInput and detaches again.
    /// AttachConsole replaces this process's standard handles with console handles that die on
    /// FreeConsole; a later conhost child would inherit them and exit at once, so they are put
    /// back. Ctrl+C is ignored only while attached (the flag is inherited by child processes).
    /// </summary>
    private bool WriteToConsole(string text)
    {
        if (_consoleClientPid == 0) return false;
        var stdIn = GetStdHandle(STD_INPUT_HANDLE);
        var stdOut = GetStdHandle(STD_OUTPUT_HANDLE);
        var stdErr = GetStdHandle(STD_ERROR_HANDLE);
        if (!AttachConsole(_consoleClientPid))
        {
            Snap.Services.Log.Warn("Terminal.WriteToConsole", $"AttachConsole({_consoleClientPid}) failed ({Marshal.GetLastWin32Error()})");
            return false;
        }
        SetConsoleCtrlHandler(IntPtr.Zero, true);
        try
        {
            var input = CreateFileW("CONIN$", GENERIC_READ_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (input == IntPtr.Zero || input == new IntPtr(-1))
            {
                Snap.Services.Log.Warn("Terminal.WriteToConsole", $"CONIN$ open failed ({Marshal.GetLastWin32Error()})");
                return false;
            }
            try
            {
                var records = new KEY_INPUT_RECORD[text.Length * 2];
                var i = 0;
                foreach (var ch in text)
                {
                    var vk = ch switch { '\r' => VK_RETURN, EscapeKey => VK_ESCAPE, _ => (ushort)0 };
                    records[i++] = new KEY_INPUT_RECORD { EventType = KEY_EVENT, bKeyDown = 1, wRepeatCount = 1, wVirtualKeyCode = vk, UnicodeChar = ch };
                    records[i++] = new KEY_INPUT_RECORD { EventType = KEY_EVENT, bKeyDown = 0, wRepeatCount = 1, wVirtualKeyCode = vk, UnicodeChar = ch };
                }
                if (!WriteConsoleInputW(input, records, (uint)records.Length, out var written) || written != records.Length)
                {
                    Snap.Services.Log.Warn("Terminal.WriteToConsole", $"WriteConsoleInput wrote {written}/{records.Length} ({Marshal.GetLastWin32Error()})");
                    return false;
                }
                return true;
            }
            finally { CloseHandle(input); }
        }
        finally
        {
            SetConsoleCtrlHandler(IntPtr.Zero, false);
            FreeConsole();
            SetStdHandle(STD_INPUT_HANDLE, stdIn);
            SetStdHandle(STD_OUTPUT_HANDLE, stdOut);
            SetStdHandle(STD_ERROR_HANDLE, stdErr);
        }
    }

    public void FocusTerminal()
    {
        if (ShellWindowHandle == IntPtr.Zero) return;
        SetForegroundWindow(ShellWindowHandle);
        // Shown again while Snap is already in front: SetForegroundWindow leaves the keyboard
        // with Snap, so also move the focus to the embedded console (its input queue is attached
        // to Snap's through the parent window).
        SetFocus(ShellWindowHandle);
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
        EmbeddedHost = hostHandle;
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
    [return: MarshalAs(UnmanagedType.Bool)]
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
    private static IntPtr FindConsoleWindow(int conhostPid, out uint clientPid, out string seen)
    {
        var pids = GetProcessFamily(conhostPid);
        IntPtr found = IntPtr.Zero;
        uint matchedPid = 0;
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
                matchedPid = windowPid;
                others.Add($"{cls}(pid {windowPid}, matched)");
                return false;
            }
            others.Add($"{cls}(pid {windowPid})");
            return true;
        }, IntPtr.Zero);

        seen = $"pids [{string.Join(",", pids)}] other windows [{string.Join(", ", others)}]";
        clientPid = matchedPid;
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
            // The user typed exit (or the shell died): close the frame and forget the shell, so
            // the next open starts a new one (#18).
            _exitedHandler = (s, e) =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(s, _shellProcess)) return;
                    Snap.Services.Log.Info("Terminal.Exited", "shell exited");
                    StopShell();
                    IsVisible = false;
                });
            };
            _shellProcess.Exited += _exitedHandler;

            _shellProcess.Start();
            var pid = _shellProcess.Id;
            LeftAsSeparateWindow = false;

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

                var hwnd = FindConsoleWindow(pid, out var clientPid, out seen);
                if (hwnd != IntPtr.Zero)
                {
                    ShellWindowHandle = hwnd;
                    _consoleClientPid = clientPid;
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
                LeftAsSeparateWindow = true;
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
        EmbeddedHost = IntPtr.Zero;
        _consoleClientPid = 0;
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
