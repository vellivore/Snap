using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Snap.Models;

namespace Snap.Services;

/// <summary>
/// The single owner of settings.json (#12).
/// <list type="bullet">
/// <item>Changes call <see cref="MarkDirty"/>; the store saves 1.5 s after the last change
/// (debounce), so a crash loses at most that window instead of the whole session.</item>
/// <item>Nothing is written until <see cref="Ready"/> is set at the end of MainWindow
/// initialization — closing during startup cannot overwrite settings with half-loaded state.</item>
/// <item>If settings.json cannot be read it is moved to <c>settings.json.bak-yyyyMMdd-HHmmss</c>
/// and saving is disabled for the rest of the session (the user sees it in the status bar).</item>
/// </list>
/// The current state is produced by <see cref="Capture"/> (set by MainWindow).
/// All members must be used from the UI thread except <see cref="MarkDirty"/>.
/// </summary>
public static class SettingsStore
{
    public static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snap");

    public static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(1.5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static DispatcherTimer? _timer;
    private static bool _dirty;

    /// <summary>Set once MainWindow has restored every piece of state. Saving is off until then.</summary>
    public static bool Ready { get; set; }

    /// <summary>True when the settings file could not be read this session; saving stays off.</summary>
    public static bool SaveDisabled { get; private set; }

    /// <summary>The status-bar message for a failed load (null when the load succeeded).</summary>
    public static string? LoadError { get; private set; }

    /// <summary>Builds the settings to persist from the live UI / view models.</summary>
    public static Func<AppSettings>? Capture { get; set; }

    /// <summary>Reads settings.json. A missing file gives defaults; an unreadable one is
    /// moved aside and disables saving for this session.</summary>
    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                   ?? throw new JsonException("settings.json is null");
        }
        catch (Exception ex)
        {
            SaveDisabled = true;
            var backup = $"{SettingsPath}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(SettingsPath, backup);
                LoadError = $"設定ファイルを読めないため {Path.GetFileName(backup)} に退避しました。今回は設定を保存しません";
                Log.UserError("SettingsStore.Load", LoadError, ex);
            }
            catch (Exception mx)
            {
                Log.Error("SettingsStore.Load", $"could not move unreadable settings aside to {backup}", mx);
                LoadError = "設定ファイルを読めません（退避にも失敗）。今回は設定を保存しません";
                Log.UserError("SettingsStore.Load", LoadError, ex);
            }
            return new AppSettings();
        }
    }

    /// <summary>Records that persisted state changed; a save follows after the debounce interval.
    /// Safe to call from any thread and before <see cref="Ready"/> (ignored then).</summary>
    public static void MarkDirty()
    {
        if (!Ready || SaveDisabled) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(MarkDirty);
            return;
        }

        _dirty = true;
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = DebounceInterval };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                if (_dirty) SaveNow();
            };
        }
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Saves immediately (window closing). No-op before <see cref="Ready"/>.</summary>
    public static void FlushNow()
    {
        _timer?.Stop();
        SaveNow();
    }

    private static void SaveNow()
    {
        if (!Ready || SaveDisabled || Capture == null) return;
        _dirty = false;

        try
        {
            var settings = Capture();
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            // Atomic write: write to a temp file then replace, so a crash/concurrent
            // write can never leave a truncated settings.json.
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("SettingsStore.Save", $"settings.json save failed: {SettingsPath}", ex);
        }
    }
}
