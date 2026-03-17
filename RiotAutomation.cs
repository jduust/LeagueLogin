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

        private const int LoginTimeoutSeconds  = 120;
        private const int KillWaitTimeoutMs    = 5000;

        // How long to wait between outer search retries (ms)
        private const int OuterRetryDelayMs = 1000;

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

                // ── 5. Fill fields ────────────────────────────────────────────
                TypeIntoField(usernameBox, username);
                TypeIntoField(passwordBox, password);

                // Press Enter to submit — more reliable than invoking the button
                // via DoDefaultAction, which doesn't always trigger React's submit.
                passwordBox.Focus();
                Keyboard.Type(VirtualKeyShort.RETURN);
                Log("Sign-in invoked.");

                // ── 6. Check + retry ──────────────────────────────────────────
                const int MaxSubmitAttempts = 3;
                const int PostSubmitWaitMs  = 3000;
                const int PreRetryDelayMs   = 500;

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
                        Log($"Login not accepted after attempt {attempt}, retrying...");
                        Thread.Sleep(PreRetryDelayMs);
                        Keyboard.Type(VirtualKeyShort.RETURN);
                        Log($"Sign-in re-invoked (attempt {attempt + 1}).");
                    }
                }

                Log("Submit timed out - re-searching...");
                Thread.Sleep(OuterRetryDelayMs);
            }

            Log("Login timeout reached.");
            return false;
        }

        // ── Input helpers ─────────────────────────────────────────────
        // Direct .Text assignment bypasses React's synthetic event system —
        // the UI sees the value but never fires onChange, so the submit button
        // stays disabled. Keyboard simulation fires proper DOM events.

        private static void TypeIntoField(AutomationElement field, string text)
        {
            field.Focus();
            // Select all existing content and replace with new text
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(text);
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