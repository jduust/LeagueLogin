using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace LeagueLogin.Services
{
    internal static class Settings
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LeagueLogin", "settings.json");

        private static SettingsData _data = new();

        static Settings() => Load();

        public static bool StartAtStartup
        {
            get => _data.StartAtStartup;
            set { _data.StartAtStartup = value; ApplyStartup(value); Save(); }
        }

        public static bool StartMinimized
        {
            get => _data.StartMinimized;
            set { _data.StartMinimized = value; Save(); }
        }

        public static bool MinimizeOnClose
        {
            get => _data.MinimizeOnClose;
            set { _data.MinimizeOnClose = value; Save(); }
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    _data = JsonSerializer.Deserialize<SettingsData>(json) ?? new();
                }
                else
                {
                    _data = new();
                }
                // Always sync with actual registry state so the checkbox is accurate
                _data.StartAtStartup = IsStartupRegistered();
            }
            catch { _data = new(); }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(_path,
                    JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private static void ApplyStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key == null) return;

                if (enable)
                {
                    var exe = Environment.ProcessPath
                        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                        ?? string.Empty;
                    key.SetValue("LeagueLogin", $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue("LeagueLogin", throwOnMissingValue: false);
                }
            }
            catch { }
        }

        private static bool IsStartupRegistered()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue("LeagueLogin") != null;
            }
            catch { return false; }
        }

        private sealed class SettingsData
        {
            public bool StartAtStartup  { get; set; }
            public bool StartMinimized  { get; set; }
            public bool MinimizeOnClose { get; set; } = true;
        }
    }
}