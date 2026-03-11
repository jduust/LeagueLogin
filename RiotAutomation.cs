using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using LeagueLogin.Services;

// Alias avoids ambiguity: FlaUI.Core.Application vs System.Windows.Forms.Application
using FlaUIApp = FlaUI.Core.Application;

namespace LeagueLogin.Services
{
    public static class RiotAutomation
    {
        private static readonly string[] ProcessesToKill = {
            "LeagueClient", "LeagueClientUx", "LeagueClientUxRender",
            "RiotClientServices", "RiotClient", "RiotClientCrashHandler",
        };

        private static readonly string[] FallbackRiotClientPaths = {
            @"C:\Riot Games\Riot Client\RiotClientServices.exe",
            @"C:\Program Files\Riot Games\Riot Client\RiotClientServices.exe",
            @"C:\Program Files (x86)\Riot Games\Riot Client\RiotClientServices.exe",
        };

        private const string LaunchArgs =
            "--launch-product=league_of_legends --launch-patchline=live --force-renderer-accessibility";

        private const int LoginTimeoutSeconds  = 120;
        private const int KillWaitTimeoutMs    = 5000;

        // How long to wait between outer search retries (ms)
        private const int OuterRetryDelayMs    = 1000;
        // How long to wait between inner submit retries (ms)
        private const int InnerRetryDelayMs    = 600;
        // How many seconds to keep retrying after the first submit attempt
        private const int PostSubmitTimeoutSec = 15;

        public static void KillLeagueProcesses()
        {
            Logger.Write("Killing Riot/League processes...");
            foreach (var name in ProcessesToKill)
                foreach (var proc in Process.GetProcessesByName(name))
                    try { proc.Kill(entireProcessTree: true); proc.WaitForExit(200); } catch { }

            var deadline = DateTime.UtcNow.AddMilliseconds(KillWaitTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                bool anyAlive = Process.GetProcesses().Any(p => {
                    try { return p.ProcessName.Contains("Riot", StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });
                if (!anyAlive) break;
                Thread.Sleep(100);
            }
            Thread.Sleep(200);
            Logger.Write("Process cleanup complete.");
        }

        public static bool LaunchRiotClient()
        {
            var exe = FindRiotClientExe();
            if (exe == null) { Logger.Write("RiotClientServices.exe not found."); return false; }
            Logger.Write("Launching: " + exe);
            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = LaunchArgs, UseShellExecute = true });
            return true;
        }

        public static bool WaitAndFillLogin(string username, string password, Action<string>? log = null)
        {
            void Log(string m) => log?.Invoke(m);
            using var automation = new UIA3Automation();
            var deadline = DateTime.UtcNow.AddSeconds(LoginTimeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                // ── 1. Find the Riot Client process ───────────────────────────
                var processes = Process.GetProcessesByName("Riot Client");
                if (processes.Length == 0)
                {
                    Log("Riot Client not found yet...");
                    Thread.Sleep(OuterRetryDelayMs);
                    continue;
                }

                // ── 2. Attach to the window ───────────────────────────────────
                FlaUIApp? app = null;
                AutomationElement? window = null;
                try
                {
                    app    = FlaUIApp.Attach(processes[0]);
                    window = app.GetMainWindow(automation);
                }
                catch (Exception ex)
                {
                    Log("Attach failed (" + ex.Message + ") - retrying...");
                    Thread.Sleep(OuterRetryDelayMs);
                    continue;
                }

                if (window == null)
                {
                    Log("Main window not ready...");
                    Thread.Sleep(OuterRetryDelayMs);
                    continue;
                }

                // ── 3. Locate the sign-in button ──────────────────────────────
                var loginButton = FindSignInButton(window, log);
                if (loginButton == null)
                {
                    Log("Sign-in button not found...");
                    Thread.Sleep(OuterRetryDelayMs);
                    continue;
                }

                // ── 4. Locate the input fields ────────────────────────────────
                var usernameBox = window.FindFirstDescendant(cf =>
                    cf.ByAutomationId("username")
                      .And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)))?.AsTextBox();
                var passwordBox = window.FindFirstDescendant(cf =>
                    cf.ByAutomationId("password")
                      .And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)))?.AsTextBox();

                if (usernameBox == null || passwordBox == null)
                {
                    Log("Input fields not found...");
                    Thread.Sleep(OuterRetryDelayMs);
                    continue;
                }

                // ── 5. Fill and submit ────────────────────────────────────────
                usernameBox.Text = username;
                passwordBox.Text = password;
                if (loginButton.Patterns.LegacyIAccessible.TryGetPattern(out var pat))
                {
                    pat.DoDefaultAction();
                    Log("Sign-in invoked.");
                }

                // ── 6. Post-submit retry loop ─────────────────────────────────
                // Re-finds the button on each iteration so a UI refresh (React
                // re-render) doesn't leave us with a stale element reference.
                var sub = DateTime.UtcNow.AddSeconds(PostSubmitTimeoutSec);
                while (DateTime.UtcNow < sub)
                {
                    Thread.Sleep(InnerRetryDelayMs);

                    // Success: both fields disappeared → login was accepted
                    var uCheck = window.FindFirstDescendant(cf =>
                        cf.ByAutomationId("username")
                          .And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)));
                    var pCheck = window.FindFirstDescendant(cf =>
                        cf.ByAutomationId("password")
                          .And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)));

                    if (uCheck == null && pCheck == null)
                    {
                        Log("Login accepted.");
                        return true;
                    }

                    // Still on login screen — re-find the button fresh each time.
                    // Riot's React renderer may have swapped out the element.
                    try
                    {
                        usernameBox.Text = username;
                        passwordBox.Text = password;
                        var freshButton = FindSignInButton(window, null);
                        if (freshButton == null)
                        {
                            // Button vanished — page is probably transitioning.
                            // Break to the outer loop to re-attach cleanly.
                            Log("Sign-in button disappeared after submit - re-searching from outer loop...");
                            break;
                        }


                        if (freshButton.Patterns.LegacyIAccessible.TryGetPattern(out var pat2))
                            pat2.DoDefaultAction();
                        Log("Re-submitted sign-in.");
                    }
                    catch (Exception ex)
                    {
                        Log("Re-submit error: " + ex.Message);
                    }
                }

                Log("Submit timed out - re-searching...");
                Thread.Sleep(OuterRetryDelayMs);
            }

            Log("Login timeout reached.");
            return false;
        }

        private static AutomationElement? FindSignInButton(AutomationElement window, Action<string>? log)
        {
            var all = window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button));
            if (all.Length == 0) return null;

            var tagged = new List<(AutomationElement E, string A)>();
            foreach (var btn in all)
                if (btn.Patterns.LegacyIAccessible.TryGetPattern(out var p) && !string.IsNullOrWhiteSpace(p.DefaultAction))
                    tagged.Add((btn, p.DefaultAction));

            if (tagged.Count == 0) return null;

            var minority = tagged.GroupBy(t => t.A, StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(g => g.Count()).First();
            var majority = tagged.GroupBy(t => t.A, StringComparer.OrdinalIgnoreCase)
                                 .OrderByDescending(g => g.Count()).First();

            if (minority.Key.Equals(majority.Key, StringComparison.OrdinalIgnoreCase)) return null;

            log?.Invoke("Sign-in outlier: " + minority.Key);
            return minority.First().E;
        }

        private static string? FindRiotClientExe()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"riotclient\shell\open\command");
                if (key?.GetValue(null) is string cmd)
                {
                    var path = cmd.Trim().TrimStart('"').Split('"')[0];
                    if (File.Exists(path)) return path;
                }
            }
            catch { }
            foreach (var p in FallbackRiotClientPaths)
                if (File.Exists(p)) return p;
            return null;
        }
    }
}
