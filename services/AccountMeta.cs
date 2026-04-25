using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LeagueLogin.Services
{
    /// <summary>
    /// Per-account usage metadata stored as JSON in %LocalAppData%\LeagueLogin\
    /// accounts.json. The Windows Credential Manager stores the secrets; this
    /// file tracks non-sensitive info like last-used timestamp and launch count
    /// so the UI can sort and display usage hints.
    ///
    /// Thread-safe: a single lock guards the dictionary and disk writes.
    /// </summary>
    internal static class AccountMeta
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LeagueLogin", "accounts.json");

        private static Dictionary<string, Entry> _data = NewDict();
        private static readonly object _lock = new();

        static AccountMeta() => Load();

        public sealed class Entry
        {
            public DateTime? LastUsedUtc { get; set; }
            public int       LaunchCount { get; set; }
        }

        // ── API ────────────────────────────────────────────────────────────

        public static Entry Get(string label)
        {
            lock (_lock) return _data.TryGetValue(label, out var e) ? e : new Entry();
        }

        public static void RecordLaunch(string label)
        {
            lock (_lock)
            {
                if (!_data.TryGetValue(label, out var e)) { e = new Entry(); _data[label] = e; }
                e.LastUsedUtc = DateTime.UtcNow;
                e.LaunchCount++;
                Save();
            }
        }

        public static void Rename(string oldLabel, string newLabel)
        {
            if (string.Equals(oldLabel, newLabel, StringComparison.OrdinalIgnoreCase)) return;
            lock (_lock)
            {
                if (_data.TryGetValue(oldLabel, out var e))
                {
                    _data[newLabel] = e;
                    _data.Remove(oldLabel);
                    Save();
                }
            }
        }

        public static void Remove(string label)
        {
            lock (_lock)
            {
                if (_data.Remove(label)) Save();
            }
        }

        // ── Presentation helpers ───────────────────────────────────────────

        /// Human-readable relative time, e.g. "just now", "3 min ago", "2 days ago".
        public static string Relative(DateTime utc)
        {
            var d = DateTime.UtcNow - utc;
            if (d.TotalSeconds < 0)       return "in the future";
            if (d.TotalSeconds < 45)      return "just now";
            if (d.TotalMinutes < 1.5)     return "1 min ago";
            if (d.TotalMinutes < 60)      return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalHours   < 1.5)     return "1 hr ago";
            if (d.TotalHours   < 24)      return $"{(int)d.TotalHours} hr ago";
            if (d.TotalDays    < 1.5)     return "yesterday";
            if (d.TotalDays    < 30)      return $"{(int)d.TotalDays} days ago";
            if (d.TotalDays    < 60)      return "1 month ago";
            if (d.TotalDays    < 365)     return $"{(int)(d.TotalDays / 30)} months ago";
            return $"{(int)(d.TotalDays / 365)} yr ago";
        }

        // ── Persistence ────────────────────────────────────────────────────

        private static Dictionary<string, Entry> NewDict() =>
            new(StringComparer.OrdinalIgnoreCase);

        private static void Load()
        {
            try
            {
                if (!File.Exists(_path)) { _data = NewDict(); return; }

                var parsed = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                    File.ReadAllText(_path));
                // Rebuild with case-insensitive comparer regardless of what was on disk.
                _data = parsed == null
                    ? NewDict()
                    : new Dictionary<string, Entry>(parsed, StringComparer.OrdinalIgnoreCase);
            }
            catch { _data = NewDict(); }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path,
                    JsonSerializer.Serialize(_data,
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* non-critical — metadata is lossy by design */ }
        }
    }
}
