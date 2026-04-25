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

        /// <summary>Account name that gets auto-logged-in on boot when AutoLoginOnBoot is enabled.</summary>
        public static string? PreferredAccount
        {
            get => _data.PreferredAccount;
            set { _data.PreferredAccount = value; Save(); ReapplyStartupIfActive(); }
        }

        /// <summary>When true, the startup registry entry gains a --boot-login flag.</summary>
        public static bool AutoLoginOnBoot
        {
            get => _data.AutoLoginOnBoot;
            set { _data.AutoLoginOnBoot = value; Save(); ReapplyStartupIfActive(); }
        }

        /// <summary>Whether to check GitHub Releases for updates on startup.</summary>
        public static bool AutoUpdateCheck
        {
            get => _data.AutoUpdateCheck;
            set { _data.AutoUpdateCheck = value; Save(); }
        }

        /// <summary>Throttle marker so we only ping the GitHub API once per day.</summary>
        public static DateTime? LastUpdateCheckUtc
        {
            get => _data.LastUpdateCheckUtc;
            set { _data.LastUpdateCheckUtc = value; Save(); }
        }

        /// <summary>Version string the user dismissed via "Skip this version".</summary>
        public static string? SkippedVersion
        {
            get => _data.SkippedVersion;
            set { _data.SkippedVersion = value; Save(); }
        }

        private static void ReapplyStartupIfActive()
        {
            if (_data.StartAtStartup) ApplyStartup(true);
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

                // MSI upgrade self-heal: if the user wants --boot-login but the
                // current Run value doesn't include it (the MSI's RunOnLogin
                // component writes a flag-less value during MajorUpgrade and
                // silently clobbers the flag we previously set), re-apply.
                if (_data.StartAtStartup &&
                    _data.AutoLoginOnBoot &&
                    !string.IsNullOrEmpty(_data.PreferredAccount) &&
                    !StartupValueContainsBootFlag())
                {
                    ApplyStartup(true);
                }
            }
            catch { _data = new(); }
        }

        private static bool StartupValueContainsBootFlag()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue("LeagueLogin") is string s &&
                       s.Contains("--boot-login", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
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

                    // Add --boot-login only when the feature is enabled AND a
                    // preferred account is selected; otherwise the flag would be
                    // a no-op at startup.
                    string args = (_data.AutoLoginOnBoot && !string.IsNullOrEmpty(_data.PreferredAccount))
                        ? " --boot-login"
                        : string.Empty;

                    key.SetValue("LeagueLogin", $"\"{exe}\"{args}");
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
            public bool      StartAtStartup     { get; set; }
            public bool      StartMinimized     { get; set; }
            public bool      MinimizeOnClose    { get; set; } = true;
            public bool      AutoLoginOnBoot    { get; set; }
            public string?   PreferredAccount   { get; set; }
            public bool      AutoUpdateCheck    { get; set; } = true;
            public DateTime? LastUpdateCheckUtc { get; set; }
            public string?   SkippedVersion     { get; set; }
        }
    }
}