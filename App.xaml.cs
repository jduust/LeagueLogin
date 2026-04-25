using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using WinForms = System.Windows.Forms;
using LeagueLogin.Services;
using Application = System.Windows.Application;
namespace LeagueLogin
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Logger.SessionStart();

            // Headless jump-list / CLI: LeagueLogin.exe --account "Name"
            if (e.Args.Length >= 2 && e.Args[0] == "--account")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                await RunHeadlessAsync(e.Args[1]);
                Shutdown();
                return;
            }

            // Boot auto-login: LeagueLogin.exe --boot-login
            // Arg is injected by Settings into the HKCU\...\Run entry only when
            // AutoLoginOnBoot is enabled and a preferred account exists.
            if (e.Args.Length >= 1 && e.Args[0] == "--boot-login")
            {
                if (TryBootLogin(out var pref))
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    await RunHeadlessAsync(pref!);
                    Shutdown();
                    return;
                }
                // Guards failed — fall through to a normal (usually minimized) start.
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var win = new MainWindow();
            if (Services.Settings.StartMinimized)
                win.Hide(); // starts in tray, window never shown
            else
                win.Show();
        }

        /// <summary>
        /// Returns true only if all boot-login guards pass:
        ///   1. A preferred account is configured.
        ///   2. No Riot Client / LeagueClient process is already running (don't
        ///      interrupt an active session if the user re-runs the app).
        ///
        /// Uptime is intentionally NOT checked: with Windows Fast Startup,
        /// shutdowns are really hibernations, so Environment.TickCount64 can be
        /// days/weeks immediately after logon. The --boot-login flag itself is
        /// the proof that this came from HKCU\...\Run, which only fires on logon.
        /// </summary>
        private static bool TryBootLogin(out string? preferred)
        {
            preferred = Services.Settings.PreferredAccount;
            if (string.IsNullOrWhiteSpace(preferred))
            {
                Logger.Write("--boot-login: no preferred account set, skipping.");
                return false;
            }

            bool alreadyRunning =
                Process.GetProcessesByName("Riot Client").Length   > 0 ||
                Process.GetProcessesByName("LeagueClient").Length  > 0 ||
                Process.GetProcessesByName("LeagueClientUx").Length > 0;
            if (alreadyRunning)
            {
                Logger.Write("--boot-login: Riot/League already running, skipping.");
                return false;
            }

            Logger.Write($"--boot-login: guards passed, logging in as {preferred}.");
            return true;
        }

        private static async Task RunHeadlessAsync(string accountName)
        {
            using var tray = new TrayManager(_ => { }, () => { }, () => { });
            tray.ShowBalloon("League Login", "Logging in as " + accountName + "...");
            Logger.Write("Headless launch for: " + accountName);

            try
            {
                var cred = CredentialStore.GetCredential(accountName);
                if (cred == null)
                {
                    tray.ShowBalloon("League Login", "Account not found.", WinForms.ToolTipIcon.Error, 3500);
                    await Task.Delay(4000); return;
                }

                RiotAutomation.KillLeagueProcesses();
                if (!RiotAutomation.LaunchRiotClient())
                {
                    tray.ShowBalloon("League Login", "Riot Client not found.", WinForms.ToolTipIcon.Error, 3500);
                    await Task.Delay(4000); return;
                }

                bool ok = await Task.Run(() =>
                    RiotAutomation.WaitAndFillLogin(cred.Value.Username, cred.Value.Password, Logger.Write));

                if (ok)
                {
                    AccountMeta.RecordLaunch(accountName);
                    tray.ShowBalloon("League Login", "Logged in as " + accountName, WinForms.ToolTipIcon.Info, 2500);
                    Logger.Write("Headless login succeeded.");
                }
                else
                {
                    tray.ShowBalloon("League Login", "Login timed out.", WinForms.ToolTipIcon.Warning, 3500);
                    Logger.Write("Headless login timed out.");
                }
                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                Logger.WriteException("Headless", ex);
                tray.ShowBalloon("League Login", "Error: " + ex.Message, WinForms.ToolTipIcon.Error, 4000);
                await Task.Delay(4500);
            }
        }
    }
}
