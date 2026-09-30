using System.IO;
using System.Text;

namespace Snap.Services;

/// <summary>
/// Append-only diagnostic log at %APPDATA%\Snap\snap.log (one line per entry).
/// Rotates to snap.log.1 when the file exceeds 1 MB (one generation kept).
/// Writes are serialized by a lock and never throw.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snap", "snap.log");

    /// <summary>
    /// Raised for failures that started from a user action (open / paste / context menu ...)
    /// so the UI can show them in the status bar. May be raised on any thread.
    /// </summary>
    public static event Action<string>? UserFacing;

    public static void Info(string where, string message, Exception? ex = null) => Write("INFO", where, message, ex);
    public static void Warn(string where, string message, Exception? ex = null) => Write("WARN", where, message, ex);
    public static void Error(string where, string message, Exception? ex = null) => Write("ERROR", where, message, ex);

    /// <summary>Logs an error and also surfaces <paramref name="userMessage"/> in the status bar.</summary>
    public static void UserError(string where, string userMessage, Exception? ex = null)
    {
        Write("ERROR", where, userMessage, ex);
        var text = ex != null ? $"{userMessage}: {ex.Message}" : userMessage;
        try { UserFacing?.Invoke(text); }
        catch (Exception hx) { Write("WARN", "Log.UserError", "status handler failed", hx); }
    }

    private static void Write(string level, string where, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(' ').Append(level)
              .Append(" [").Append(where).Append("] ")
              .Append(Flatten(message));
            if (ex != null)
                sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(Flatten(ex.ToString()));
            var line = sb.ToString();

            System.Diagnostics.Debug.WriteLine(line);

            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                File.AppendAllText(LogPath, line + Environment.NewLine, Utf8NoBom);
            }
        }
        catch
        {
            // Logging must never throw or recurse.
        }
    }

    private static string Flatten(string s) =>
        s.Replace("\r\n", " ⏎ ").Replace('\n', ' ').Replace('\r', ' ');
}
