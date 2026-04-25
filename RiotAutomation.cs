using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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

        // Relative subpaths under a root (drive or Program Files dir) where Riot
        // Client is commonly installed. Combined with drive enumeration below to
        // cover non-C installs.
        private static readonly string[] RiotClientSubPaths = {
            @"Riot Games\Riot Client\RiotClientServices.exe",
            @"Program Files\Riot Games\Riot Client\RiotClientServices.exe",
            @"Program Files (x86)\Riot Games\Riot Client\RiotClientServices.exe",
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
                // ── 1. Find process ───────────────────────────────────────────
                var processes = Process.GetProcessesByName("Riot Client");
                if (processes.Length == 0)
                {
                    Log("Riot Client not found yet...");
                    Thread.Sleep(OuterRetryDelayMs);
                    continue;
                }

                // ── 2. Attach ─────────────────────────────────────────────────
                AutomationElement? window = null;
                try
                {
                    var app = FlaUIApp.Attach(processes[0]);
                    window  = app.GetMainWindow(automation);
                }
                catch (Exception ex) { Log("Attach failed (" + ex.Message + ")"); Thread.Sleep(OuterRetryDelayMs); continue; }

                if (window == null) { Log("Main window not ready..."); Thread.Sleep(OuterRetryDelayMs); continue; }

                var walker = automation.TreeWalkerFactory.GetControlViewWalker();

                // ── 3. Find fields ────────────────────────────────────────────
                var userField = FindByAutomationId(window, "username", walker);
                var passField = FindByAutomationId(window, "password",  walker);

                if (userField == null || passField == null)
                {
                    Log("Login fields not found yet...");
                    Thread.Sleep(OuterRetryDelayMs);
                    continue;
                }

                // ── 4. Fill fields via ValuePattern (no keyboard/foreground) ──
                Log("Filling username...");
                if (!SetFieldValue(userField, username, Log))  { Thread.Sleep(OuterRetryDelayMs); continue; }
                Thread.Sleep(150);

                Log("Filling password...");
                if (!SetFieldValue(passField, password, Log))  { Thread.Sleep(OuterRetryDelayMs); continue; }
                Thread.Sleep(150);

                // ── 5. Submit via Enter on whatever field is focused ──────────
                // No Focus() call — that activates the window on Chromium. The
                // login screen auto-focuses a field on load, so focus is already
                // inside the form; SendEnterBackground resolves the focused
                // HWND and posts directly to it.
                var hwnd = new IntPtr(window.Properties.NativeWindowHandle.Value);
                SendEnterBackground(hwnd, Log);
                Log("Sign-in invoked (Enter).");

                // Short probe: if fields disappear quickly, Enter worked.
                bool enterWorked = false;
                var enterDeadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < enterDeadline)
                {
                    Thread.Sleep(300);
                    if (FindByAutomationId(window, "username", walker) == null)
                    {
                        enterWorked = true;
                        break;
                    }
                }

                if (enterWorked) { Log("Login accepted (Enter)."); return true; }

                // ── 6. Fallback: find + invoke login button ───────────────────
                Log("Enter didn't submit — falling back to button invoke.");
                AutomationElement? loginBtn = null;
                var btnDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < btnDeadline)
                {
                    loginBtn = FindNamelessActionButton(window, walker, Log);
                    if (loginBtn != null) break;
                    Log("Login button not enabled yet...");
                    Thread.Sleep(500);
                }

                if (loginBtn == null) { Log("Login button not found — retrying from scratch..."); Thread.Sleep(OuterRetryDelayMs); continue; }

                Log("Invoking login button...");
                if (loginBtn.Patterns.Invoke.TryGetPattern(out var invokePat))
                    invokePat.Invoke();
                else if (loginBtn.Patterns.LegacyIAccessible.TryGetPattern(out var loginLegacy))
                    loginLegacy.DoDefaultAction();
                else
                    loginBtn.Click();

                Log("Sign-in invoked (button).");

                // ── 7. Wait for login fields to disappear ─────────────────────
                var submitDeadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < submitDeadline)
                {
                    Thread.Sleep(1000);
                    var check = FindByAutomationId(window, "username", walker);
                    if (check == null) { Log("Login accepted."); return true; }
                }

                Log("Login not accepted — retrying...");
            }

            Log("Login timeout reached.");
            return false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP   = 0x0101;
        private const uint WM_CHAR    = 0x0102;
        private const int  VK_RETURN  = 0x0D;

        /// Sends Enter to the Riot window in the background. Targets the focused
        /// HWND in the window's UI thread (Chromium hosts a child HWND for input),
        /// falling back to the top-level HWND. Also posts WM_CHAR since some
        /// Chromium builds ignore WM_KEYDOWN/KEYUP but dispatch WM_CHAR to the
        /// focused widget.
        private static void SendEnterBackground(IntPtr topHwnd, Action<string> log)
        {
            var target = topHwnd;
            try
            {
                uint tid = GetWindowThreadProcessId(topHwnd, out _);
                var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero)
                    target = gti.hwndFocus;
            }
            catch (Exception ex) { log("GetGUIThreadInfo failed: " + ex.Message); }

            PostMessage(target, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)0x001C0001);
            PostMessage(target, WM_CHAR,    (IntPtr)VK_RETURN, (IntPtr)0x001C0001);
            PostMessage(target, WM_KEYUP,   (IntPtr)VK_RETURN, unchecked((IntPtr)0xC01C0001));
        }

        /// Sets an edit field's value via ValuePattern (no keyboard, no foreground needed).
        /// Falls back to DoDefaultAction + SetValue if the pattern isn't directly available.
        private static bool SetFieldValue(AutomationElement field, string value, Action<string> log)
        {
            try
            {
                if (field.Patterns.Value.TryGetPattern(out var vp))
                {
                    vp.SetValue(value);
                    return true;
                }
                // Fallback: activate the field then try again
                if (field.Patterns.LegacyIAccessible.TryGetPattern(out var leg))
                    leg.DoDefaultAction();
                Thread.Sleep(80);
                if (field.Patterns.Value.TryGetPattern(out var vp2))
                {
                    vp2.SetValue(value);
                    return true;
                }
                log("SetFieldValue: ValuePattern unavailable.");
                return false;
            }
            catch (Exception ex) { log("SetFieldValue failed: " + ex.Message); return false; }
        }

        /// Finds the sign-in button: a Button that has a DefaultAction but NO Name.
        /// That's exactly what Accessibility Insights reported for the Riot login button.
        private static AutomationElement? FindNamelessActionButton(
            AutomationElement root, FlaUI.Core.ITreeWalker walker, Action<string>? log)
        {
            var candidates = new List<AutomationElement>();
            CollectButtons(root, walker, candidates, 0);

            // Primary: nameless button with a DefaultAction (the submit button)
            var nameless = candidates.Where(b =>
            {
                try
                {
                    string? n = null;
                    try { n = b.Name; } catch { }
                    if (!string.IsNullOrEmpty(n)) return false;

                    if (!b.Patterns.LegacyIAccessible.TryGetPattern(out var p)) return false;
                    return !string.IsNullOrWhiteSpace(p.DefaultAction);
                }
                catch { return false; }
            }).ToList();

            if (nameless.Count == 1) return nameless[0];

            // Fallback: original minority-DefaultAction heuristic
            var tagged = new List<(AutomationElement E, string A)>();
            foreach (var btn in candidates)
            {
                try
                {
                    if (btn.Patterns.LegacyIAccessible.TryGetPattern(out var p) &&
                        !string.IsNullOrWhiteSpace(p.DefaultAction))
                        tagged.Add((btn, p.DefaultAction));
                }
                catch { }
            }

            if (tagged.Count == 0) return null;

            var minority = tagged.GroupBy(t => t.A, StringComparer.OrdinalIgnoreCase)
                                .OrderBy(g => g.Count()).First();
            var majority = tagged.GroupBy(t => t.A, StringComparer.OrdinalIgnoreCase)
                                .OrderByDescending(g => g.Count()).First();

            if (minority.Key.Equals(majority.Key, StringComparison.OrdinalIgnoreCase)) return null;

            log?.Invoke("Sign-in outlier: " + minority.Key);
            return minority.First().E;
        }

        /// Recursively collects all Button elements via TreeWalker (crosses Chrome frames).
        private static void CollectButtons(
            AutomationElement root, FlaUI.Core.ITreeWalker walker,
            List<AutomationElement> result, int depth)
        {
            if (depth > 25) return;
            try
            {
                if (root.ControlType == FlaUI.Core.Definitions.ControlType.Button)
                    result.Add(root);
            }
            catch { }

            AutomationElement? child = null;
            try { child = walker.GetFirstChild(root); } catch { return; }
            while (child != null)
            {
                CollectButtons(child, walker, result, depth + 1);
                AutomationElement? next = null;
                try { next = walker.GetNextSibling(child); } catch { break; }
                child = next;
            }
        }

        /// Finds the first element with the given AutomationId via TreeWalker.
        private static AutomationElement? FindByAutomationId(
            AutomationElement root, string id, FlaUI.Core.ITreeWalker walker, int depth = 0)
        {
            if (depth > 25) return null;
            try
            {
                if (root.AutomationId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
                    return root;
            }
            catch { }

            AutomationElement? child = null;
            try { child = walker.GetFirstChild(root); } catch { return null; }
            while (child != null)
            {
                var found = FindByAutomationId(child, id, walker, depth + 1);
                if (found != null) return found;
                AutomationElement? next = null;
                try { next = walker.GetNextSibling(child); } catch { break; }
                child = next;
            }
            return null;
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
            // 1. Canonical: Riot's own install metadata at ProgramData.
            //    This file is written by the Riot installer on every machine
            //    regardless of chosen install directory, so it handles D:, custom
            //    paths, etc. without guessing.
            try
            {
                var metaPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Riot Games", "RiotClientInstalls.json");
                if (File.Exists(metaPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                    foreach (var key in new[] { "rc_live", "rc_default", "rc_beta" })
                    {
                        if (doc.RootElement.TryGetProperty(key, out var val) &&
                            val.ValueKind == JsonValueKind.String)
                        {
                            var p = val.GetString();
                            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                        }
                    }
                }
            }
            catch { }

            // 2. Registry-registered handler for riotclient:// URIs.
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

            // 3. Scan every fixed drive for common subpaths (C:, D:, E:, ...).
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                foreach (var sub in RiotClientSubPaths)
                {
                    var candidate = Path.Combine(drive.RootDirectory.FullName, sub);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            return null;
        }

        private static bool IsLeagueClientRunning()
        {
            return Process.GetProcessesByName("LeagueClient").Length > 0 ||
                   Process.GetProcessesByName("LeagueClientUx").Length > 0;
        }

        private enum LaunchState { None, Play, Update, InProgress }

        // Exact button Names (uppercased) that we should click to advance.
        private static readonly string[] ActionableNames = { "PLAY", "UPDATE", "INSTALL", "CONTINUE" };

        // Substrings (uppercased) that indicate a patch is running and the button
        // should NOT be clicked — we wait for it to transition to Play.
        private static readonly string[] ProgressSubstrings = {
            "DOWNLOAD", "PREPAR", "UPDATING", "INSTALLING", "VERIF", "PATCH", "INITIAL"
        };

        private const int PlayInitialTimeoutSeconds   = 30;
        private const int PatchTimeoutSeconds         = 2 * 60 * 60; // 2h — generous cap for large patches
        private const int PlayLaunchTimeoutSeconds    = 5 * 60;      // after clicking Play, how long to wait for LeagueClient
        private const int PlayReclickIntervalSeconds  = 3;           // minimum gap between re-clicks of Play

        public static bool WaitAndClickPlay(Action<string>? log = null)
        {
            void Log(string m) => log?.Invoke(m);
            using var automation = new UIA3Automation();

            var deadline         = DateTime.UtcNow.AddSeconds(PlayInitialTimeoutSeconds);
            bool patchMode       = false;
            bool dumpedDiagnostic = false;
            string lastProgressName = "";
            DateTime lastProgressLog = DateTime.MinValue;

            // Post-click verification state: once Play has been invoked, we keep
            // looping (up to PlayLaunchTimeoutSeconds) to confirm the LeagueClient
            // process appears. If Play re-appears (click didn't register), re-click
            // — but not more often than PlayReclickIntervalSeconds.
            bool     playInvoked  = false;
            int      playClicks   = 0;
            DateTime lastPlayClick = DateTime.MinValue;

            Log("Waiting for Play button on Riot Client home screen...");

            while (DateTime.UtcNow < deadline)
            {
                // If League is already up, we're done — the Riot Client's home
                // screen is now irrelevant.
                if (IsLeagueClientRunning())
                {
                    Log("LeagueClient detected — launch successful.");
                    return true;
                }

                var processes = Process.GetProcessesByName("Riot Client");
                if (processes.Length == 0) { Thread.Sleep(1000); continue; }

                try
                {
                    var app    = FlaUIApp.Attach(processes[0]);
                    var window = app.GetMainWindow(automation);
                    if (window == null) { Thread.Sleep(1000); continue; }

                    var walker = automation.TreeWalkerFactory.GetControlViewWalker();
                    var (state, element, stateName) = ClassifyLaunchArea(window, walker);

                    switch (state)
                    {
                        case LaunchState.Play:
                        {
                            double sinceLast = (DateTime.UtcNow - lastPlayClick).TotalSeconds;
                            if (!playInvoked)
                            {
                                Log("Play button found — clicking.");
                                InvokeElement(element!);
                                playClicks++;
                                lastPlayClick = DateTime.UtcNow;
                                playInvoked   = true;
                                // Give LeagueClient a generous window to appear.
                                deadline = DateTime.UtcNow.AddSeconds(PlayLaunchTimeoutSeconds);
                                Log("Play invoked — verifying LeagueClient launches...");
                            }
                            else if (sinceLast >= PlayReclickIntervalSeconds)
                            {
                                // Play is still showing after a prior click — the
                                // click didn't land (Riot Client sometimes eats
                                // clicks if the button rendered too early). Try again.
                                playClicks++;
                                Log($"LeagueClient not running and Play still visible — re-clicking (attempt {playClicks}).");
                                InvokeElement(element!);
                                lastPlayClick = DateTime.UtcNow;
                                deadline = DateTime.UtcNow.AddSeconds(PlayLaunchTimeoutSeconds);
                            }
                            // Fall through to the loop's bottom sleep; next
                            // iteration checks IsLeagueClientRunning at the top.
                            break;
                        }

                        case LaunchState.Update:
                            Log($"Action button '{stateName}' found — clicking to start patch.");
                            InvokeElement(element!);
                            if (!patchMode)
                            {
                                patchMode = true;
                                deadline = DateTime.UtcNow.AddSeconds(PatchTimeoutSeconds);
                                Log("Entering patch-wait mode (timeout extended).");
                            }
                            if (!dumpedDiagnostic)
                            {
                                DumpUiTreeToDesktop(window, walker, "patch-update", Log);
                                dumpedDiagnostic = true;
                            }
                            Thread.Sleep(2000);
                            continue;

                        case LaunchState.InProgress:
                            if (!patchMode)
                            {
                                patchMode = true;
                                deadline = DateTime.UtcNow.AddSeconds(PatchTimeoutSeconds);
                                Log("Patch in progress — entering patch-wait mode.");
                            }
                            if (!dumpedDiagnostic)
                            {
                                DumpUiTreeToDesktop(window, walker, "patch-progress", Log);
                                dumpedDiagnostic = true;
                            }
                            if (stateName != lastProgressName ||
                                (DateTime.UtcNow - lastProgressLog).TotalSeconds > 30)
                            {
                                Log("Patch state: " + stateName);
                                lastProgressName = stateName;
                                lastProgressLog  = DateTime.UtcNow;
                            }
                            Thread.Sleep(2000);
                            continue;

                        default:
                            if (!patchMode) Log("Play button not visible yet...");
                            break;
                    }
                }
                catch (Exception ex) { Log("WaitAndClickPlay: " + ex.Message); }

                Thread.Sleep(patchMode ? 2000 : 1000);
            }

            // One last success check — League may have launched during the final
            // sleep tick, in which case we shouldn't report timeout.
            if (IsLeagueClientRunning())
            {
                Log("LeagueClient detected at deadline — launch successful.");
                return true;
            }

            // Pick the dump tag + log message for whichever phase we timed out in.
            string dumpReason;
            string logMessage;
            if (playInvoked)
            {
                dumpReason = "post-play-no-launch";
                logMessage = $"Play was clicked {playClicks}x but LeagueClient never started. Giving up.";
            }
            else if (patchMode)
            {
                dumpReason = "patch-timeout";
                logMessage = "Patch wait timed out.";
            }
            else
            {
                dumpReason = "play-timeout";
                logMessage = "Timed out — falling back to re-invocation.";
            }

            // Dump the current UI tree so the user can share it for diagnosis.
            try
            {
                var procs = Process.GetProcessesByName("Riot Client");
                if (procs.Length > 0)
                {
                    var app    = FlaUIApp.Attach(procs[0]);
                    var window = app.GetMainWindow(automation);
                    if (window != null)
                    {
                        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
                        DumpUiTreeToDesktop(window, walker, dumpReason, Log);
                    }
                }
            }
            catch (Exception ex) { Log("Timeout dump failed: " + ex.Message); }

            Log(logMessage);
            return false;
        }

        // ── UI tree diagnostic dump ───────────────────────────────────────────

        private static void DumpUiTreeToDesktop(
            AutomationElement root, FlaUI.Core.ITreeWalker walker,
            string reason, Action<string> log)
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var stamp   = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var path    = Path.Combine(desktop, $"LeagueLogin-ui-{reason}-{stamp}.txt");

                var sb = new StringBuilder();
                sb.AppendLine($"# Riot Client UI tree — reason: {reason}");
                sb.AppendLine($"# Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
                DumpElementRecursive(root, walker, sb, 0);

                File.WriteAllText(path, sb.ToString());
                log($"UI tree dumped: {path}");
            }
            catch (Exception ex) { log("UI dump failed: " + ex.Message); }
        }

        private static void DumpElementRecursive(
            AutomationElement el, FlaUI.Core.ITreeWalker walker,
            StringBuilder sb, int depth)
        {
            if (depth > 30) return;

            string name = "", id = "", type = "", enabled = "", value = "", defAction = "";
            try { name    = el.Name ?? ""; } catch { }
            try { id      = el.AutomationId ?? ""; } catch { }
            try { type    = el.ControlType.ToString(); } catch { }
            try { enabled = el.IsEnabled.ToString(); } catch { }
            try
            {
                // Skip password-field values so credentials can't leak into a dump
                // even if someone runs this while the login form is visible.
                if (!id.Equals("password", StringComparison.OrdinalIgnoreCase) &&
                    el.Patterns.Value.TryGetPattern(out var vp))
                    value = vp.Value.Value ?? "";
            }
            catch { }
            try
            {
                if (el.Patterns.LegacyIAccessible.TryGetPattern(out var leg))
                    defAction = leg.DefaultAction ?? "";
            }
            catch { }

            sb.Append(new string(' ', depth * 2));
            sb.Append('[').Append(type).Append(']');
            if (!string.IsNullOrEmpty(name))      sb.Append(" Name=\"").Append(Escape(name)).Append('"');
            if (!string.IsNullOrEmpty(id))        sb.Append(" AutomationId=\"").Append(Escape(id)).Append('"');
            if (!string.IsNullOrEmpty(enabled))   sb.Append(" IsEnabled=").Append(enabled);
            if (!string.IsNullOrEmpty(value))     sb.Append(" Value=\"").Append(Escape(value)).Append('"');
            if (!string.IsNullOrEmpty(defAction)) sb.Append(" DefaultAction=\"").Append(Escape(defAction)).Append('"');
            sb.AppendLine();

            AutomationElement? child = null;
            try { child = walker.GetFirstChild(el); } catch { return; }
            while (child != null)
            {
                DumpElementRecursive(child, walker, sb, depth + 1);
                AutomationElement? next = null;
                try { next = walker.GetNextSibling(child); } catch { break; }
                child = next;
            }
        }

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

        private static (LaunchState state, AutomationElement? el, string name) ClassifyLaunchArea(
            AutomationElement root, FlaUI.Core.ITreeWalker walker)
        {
            var buttons = new List<AutomationElement>();
            CollectButtons(root, walker, buttons, 0);

            AutomationElement? progressBtn = null;
            string progressName = "";

            foreach (var btn in buttons)
            {
                string? name;
                try { name = btn.Name; } catch { continue; }
                if (string.IsNullOrWhiteSpace(name)) continue;

                var upper = name.Trim().ToUpperInvariant();

                if (upper == "PLAY") return (LaunchState.Play, btn, name);

                if (Array.IndexOf(ActionableNames, upper) >= 0)
                {
                    bool enabled = true;
                    try { enabled = btn.IsEnabled; } catch { }
                    if (enabled) return (LaunchState.Update, btn, name);
                }

                foreach (var sub in ProgressSubstrings)
                {
                    if (upper.Contains(sub))
                    {
                        progressBtn = btn;
                        progressName = name;
                        break;
                    }
                }
            }

            return progressBtn != null
                ? (LaunchState.InProgress, progressBtn, progressName)
                : (LaunchState.None, null, "");
        }

        private static void InvokeElement(AutomationElement el)
        {
            if (el.Patterns.Invoke.TryGetPattern(out var inv))
                inv.Invoke();
            else if (el.Patterns.LegacyIAccessible.TryGetPattern(out var legacy))
                legacy.DoDefaultAction();
            else
                el.Click();
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