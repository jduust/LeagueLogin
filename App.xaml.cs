using System;
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

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new MainWindow().Show();
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
