using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snap.Models;

namespace Snap.Services;

public static class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snap");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // Corrupted JSON or any other error → default
            Log.Error("SettingsService.Load", $"settings.json unreadable, using defaults: {SettingsPath}", ex);
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            // Atomic write: write to a temp file then replace, so a crash/concurrent
            // write can never leave a truncated settings.json that resets the user's
            // window layout / bookmarks / tabs on next load.
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Write failure → keep running, but leave a trace.
            Log.Error("SettingsService.Save", $"settings.json save failed: {SettingsPath}", ex);
        }
    }
}
