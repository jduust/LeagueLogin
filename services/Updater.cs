using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LeagueLogin.Services
{
    /// <summary>
    /// Checks GitHub Releases for newer versions and (optionally) downloads &
    /// runs the MSI. Anonymous GitHub API calls are rate-limited to 60/hour
    /// per IP, but we throttle to ~once per day so this is not a concern.
    /// </summary>
    public sealed class UpdateInfo
    {
        public Version Version       { get; init; } = new(0, 0, 0, 0);
        public string  TagName       { get; init; } = "";
        public string  Title         { get; init; } = "";
        public string  Body          { get; init; } = "";
        public string  ReleaseUrl    { get; init; } = "";
        public string? MsiAssetUrl   { get; init; }
        public string? MsiAssetName  { get; init; }
        public long    MsiAssetSize  { get; init; }
    }

    public static class Updater
    {
        private const string Owner  = "jduust";
        private const string Repo   = "LeagueLogin";
        private const string ApiUrl = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

        // Single static HttpClient — recommended pattern for app-lifetime usage.
        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // GitHub requires a User-Agent header on all API calls.
            c.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("LeagueLogin", CurrentVersion().ToString()));
            c.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return c;
        }

        /// <summary>
        /// Returns the running assembly's version normalized to four parts (so
        /// 3-part GitHub tags like "v1.2.0" compare correctly against the
        /// 4-part assembly version "1.2.0.0").
        /// </summary>
        public static Version CurrentVersion()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version();
            return Normalize(v);
        }

        public static bool IsNewer(UpdateInfo info) => info.Version > CurrentVersion();

        // ── Network ────────────────────────────────────────────────────────

        public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync(ApiUrl, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    Logger.Write($"Updater: GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase}.");
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tag   = TryGetString(root, "tag_name") ?? "";
                if (!TryParseVersion(tag, out var ver)) return null;

                string? msiUrl = null, msiName = null;
                long    msiSize = 0;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = TryGetString(a, "name") ?? "";
                        if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        {
                            msiName = name;
                            msiUrl  = TryGetString(a, "browser_download_url");
                            if (a.TryGetProperty("size", out var s) &&
                                s.ValueKind == JsonValueKind.Number)
                                msiSize = s.GetInt64();
                            break;
                        }
                    }
                }

                return new UpdateInfo
                {
                    Version      = ver,
                    TagName      = tag,
                    Title        = TryGetString(root, "name") ?? tag,
                    Body         = TryGetString(root, "body") ?? "",
                    ReleaseUrl   = TryGetString(root, "html_url") ?? "",
                    MsiAssetUrl  = msiUrl,
                    MsiAssetName = msiName,
                    MsiAssetSize = msiSize,
                };
            }
            catch (Exception ex)
            {
                Logger.WriteException("Updater.CheckAsync", ex);
                return null;
            }
        }

        public static async Task<string?> DownloadAsync(
            UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(info.MsiAssetUrl) || string.IsNullOrEmpty(info.MsiAssetName))
            {
                Logger.Write("Updater: release has no MSI asset, can't auto-install.");
                return null;
            }

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LeagueLogin", "Updates");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, info.MsiAssetName);

            try
            {
                using var resp = await _http.GetAsync(
                    info.MsiAssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength ?? info.MsiAssetSize;
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var file   = File.Create(path);

                var buffer = new byte[81920];
                long got = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    got += read;
                    if (total > 0) progress?.Report((double)got / total);
                }

                Logger.Write($"Updater: downloaded {got} bytes to {path}");
                return path;
            }
            catch (Exception ex)
            {
                Logger.WriteException("Updater.DownloadAsync", ex);
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                return null;
            }
        }

        /// <summary>
        /// Spawns a detached cmd that waits ~2 s for the current process to
        /// exit before launching msiexec. This avoids "files in use" errors
        /// when the MSI tries to replace the running exe.
        /// </summary>
        public static void RunInstallerAndExit(string msiPath)
        {
            try
            {
                var args = $"/c timeout /t 2 /nobreak >nul && msiexec /i \"{msiPath}\" /passive";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "cmd.exe",
                    Arguments       = args,
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                });
                Logger.Write("Updater: installer spawned, exiting app.");
            }
            catch (Exception ex)
            {
                Logger.WriteException("Updater.RunInstaller", ex);
            }

            System.Windows.Application.Current?.Shutdown();
        }

        public static void OpenReleaseInBrowser(UpdateInfo info)
        {
            if (string.IsNullOrEmpty(info.ReleaseUrl)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = info.ReleaseUrl,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Logger.WriteException("Updater.OpenRelease", ex); }
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private static bool TryParseVersion(string tag, out Version ver)
        {
            ver = new Version();
            var s = tag.TrimStart('v', 'V').Trim();
            if (!Version.TryParse(s, out var parsed)) return false;
            ver = Normalize(parsed);
            return true;
        }

        private static Version Normalize(Version v) => new(
            v.Major,
            Math.Max(v.Minor,    0),
            Math.Max(v.Build,    0),
            Math.Max(v.Revision, 0));

        private static string? TryGetString(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
