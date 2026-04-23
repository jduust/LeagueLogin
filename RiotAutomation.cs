using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
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

        private const int LoginTimeoutSeconds = 120;
        private const int KillWaitTimeoutMs   = 5000;
        private const int OuterRetryDelayMs   = 1000;

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

        /// <summary>
        /// Re-invokes RiotClientServices with the League launch args.
        /// Because Riot Client is single-instance, the second invocation passes
        /// the --launch-product request to the already-running client, which then
        /// queues League to start — identical to clicking the desktop shortcut.
        /// </summary>
        public static void LaunchLeague()
        {
            var exe = FindRiotClientExe();
            if (exe == null) { Logger.Write("LaunchLeague: exe not found."); return; }

            Logger.Write("Re-invoking Riot Client to launch League...");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName       = exe,
                    Arguments      = "--launch-product=league_of_legends --launch-patchline=live",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Logger.WriteException("LaunchLeague", ex); }
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

                // ── 3. Wait for login page to be ready ────────────────────────
                // FindSignInButton is a page-readiness check — if the button
                // isn't there yet the login screen hasn't fully loaded.
                if (FindSignInButton(window, log) == null)
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
                FillAndSubmit(usernameBox, passwordBox, username, password, 1, log);
                Log("Sign-in invoked.");

                // ── 6. Check + retry ──────────────────────────────────────────
                const int MaxSubmitAttempts = 3;
                const int PostSubmitWaitMs  = 3000;

                for (int attempt = 1; attempt <= MaxSubmitAttempts; attempt++)
                {
                    Thread.Sleep(PostSubmitWaitMs);

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

                    if (attempt < MaxSubmitAttempts)
                    {
                        int nextAttempt = attempt + 1;
                        bool slow = nextAttempt >= 3;
                        Log($"Login not accepted after attempt {attempt}, clearing and retrying (attempt {nextAttempt}{(slow ? ", slow mode" : "")})...");

                        // Clear fields first so React resets state cleanly
                        ClearField(usernameBox, click: slow);
                        ClearField(passwordBox, click: slow);
                        Thread.Sleep(slow ? 300 : 100);

                        FillAndSubmit(usernameBox, passwordBox, username, password, nextAttempt, log);
                        Log($"Sign-in re-invoked (attempt {nextAttempt}).");
                    }
                }

                Log("Submit timed out - re-searching...");
                Thread.Sleep(OuterRetryDelayMs);
            }

            Log("Login timeout reached.");
            return false;
        }

        // ── Input helpers ─────────────────────────────────────────────────────

        private static void FillAndSubmit(
            AutomationElement usernameBox, AutomationElement passwordBox,
            string username, string password, int attempt, Action<string>? log)
        {
            if (attempt <= 2)
            {
                // Attempts 1 & 2: fast — Focus() only, minimal delays
                TypeIntoField(usernameBox, username, click: false);
                Thread.Sleep(attempt == 1 ? 100 : 150);
                TypeIntoField(passwordBox, password, click: false);
                Thread.Sleep(attempt == 1 ? 100 : 150);
                passwordBox.Focus();
            }
            else
            {
                // Attempt 3: slow fallback — Click() each field, longer delays
                TypeIntoField(usernameBox, username, click: true);
                Thread.Sleep(250);
                TypeIntoField(passwordBox, password, click: true);
                Thread.Sleep(300);
                passwordBox.Click();
            }

            Keyboard.Type(VirtualKeyShort.RETURN);
        }

        private static void ClearField(AutomationElement field, bool click)
        {
            if (click) field.Click(); else field.Focus();
            Thread.Sleep(50);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(VirtualKeyShort.DELETE);
        }

        private static void TypeIntoField(AutomationElement field, string text, bool click)
        {
            if (click) field.Click(); else field.Focus();
            Thread.Sleep(click ? 80 : 40);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Thread.Sleep(click ? 50 : 20);
            Keyboard.Type(text);
        }

        // ── Sign-in button heuristic (also used as page-readiness check) ──────

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

        public static bool WaitAndClickPlay(Action<string>? log = null)
        {
            void Log(string m) => log?.Invoke(m);
            using var automation = new UIA3Automation();
            var deadline = DateTime.UtcNow.AddSeconds(30);

            Log("Waiting for Play button on Riot Client home screen...");

            while (DateTime.UtcNow < deadline)
            {
                var processes = Process.GetProcessesByName("Riot Client");
                if (processes.Length == 0) { Thread.Sleep(1000); continue; }

                try
                {
                    var app    = FlaUIApp.Attach(processes[0]);
                    var window = app.GetMainWindow(automation);
                    if (window == null) { Thread.Sleep(1000); continue; }

                    var walker  = automation.TreeWalkerFactory.GetControlViewWalker();
                    var playBtn = FindByNameAndType(
                        window, "Play",
                        FlaUI.Core.Definitions.ControlType.Button,
                        walker);

                    if (playBtn != null)
                    {
                        Log("Play button found — clicking.");
                        
                        // DoDefaultAction() goes through the IAccessible2 proxy directly —
                        // no clickable-point calculation needed, works even when the window
                        // isn't foregrounded.
                        if (playBtn.Patterns.LegacyIAccessible.TryGetPattern(out var legacy))
                        {
                            legacy.DoDefaultAction();
                            Log("DoDefaultAction invoked on Play button.");
                        }
                        else
                        {
                            // Should never reach here given the accessibility data you shared,
                            // but fall back to a regular click just in case.
                            playBtn.Click();
                        }
                        return true;
                    }

                    Log("Play button not visible yet...");
                }
                catch (Exception ex) { Log("WaitAndClickPlay: " + ex.Message); }

                Thread.Sleep(1000);
            }

            Log("Timed out — falling back to re-invocation.");
            return false;
        }

        // Walks the full UIA tree recursively, including Chrome-hosted content
        // that FindAllDescendants() silently skips.
        private static AutomationElement? FindByNameAndType(
            AutomationElement root,
            string name,
            FlaUI.Core.Definitions.ControlType type,
            FlaUI.Core.ITreeWalker walker,
            int depth = 0)
        {
            if (depth > 25) return null;   // guard against pathological trees

            try
            {
                if (root.ControlType == type &&
                    root.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                    return root;
            }
            catch { /* element may have vanished */ }

            AutomationElement? child = null;
            try { child = walker.GetFirstChild(root); }
            catch { return null; }

            while (child != null)
            {
                var found = FindByNameAndType(child, name, type, walker, depth + 1);
                if (found != null) return found;

                AutomationElement? next = null;
                try { next = walker.GetNextSibling(child); } catch { break; }
                child = next;
            }
            return null;
        }
    }
}