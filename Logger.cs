using System;
using System.IO;

namespace LeagueLogin.Services
{
    /// <summary>
    /// Lightweight file logger. Logs land in %LocalAppData%\LeagueLogin\debug.log.
    /// They are never shown in the UI automatically; the user can open them via
    /// the tray icon → "View Logs" menu item.
    /// </summary>
    internal static class Logger
    {
        private static readonly string _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LeagueLogin");

        public static string LogPath { get; } = Path.Combine(_logDir, "debug.log");

        static Logger()
        {
            try { Directory.CreateDirectory(_logDir); }
            catch { /* non-fatal */ }

            // Rotate if the log gets large (> 2 MB)
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    fi.Delete();
            }
            catch { }
        }

        public static void Write(string message)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
            }
            catch { /* never crash the caller */ }
        }

        public static void WriteException(string context, Exception ex)
            => Write($"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

        public static void SessionStart()
        {
            Write(new string('─', 60));
            Write($"Session started  |  v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        }
    }
}
