using System.Windows;
using System.Threading.Tasks;
using LeagueLogin.Services;

namespace LeagueLogin
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            // Load current values — set IsChecked without firing the Changed events
            ChkStartup.IsChecked          = Settings.StartAtStartup;
            ChkStartMinimized.IsChecked   = Settings.StartMinimized;
            ChkAutoLoginOnBoot.IsChecked  = Settings.AutoLoginOnBoot;
            ChkMinimizeOnClose.IsChecked  = Settings.MinimizeOnClose;
            ChkAutoUpdate.IsChecked       = Settings.AutoUpdateCheck;
            UpdatePreferredLabel();
            UpdateStatus.Text =
                $"You're on v{Updater.CurrentVersion()}.";
        }

        private void ChkStartup_Changed(object sender, RoutedEventArgs e)
        {
            Settings.StartAtStartup = ChkStartup.IsChecked == true;
            UpdatePreferredLabel();
        }

        private void ChkStartMinimized_Changed(object sender, RoutedEventArgs e)
            => Settings.StartMinimized = ChkStartMinimized.IsChecked == true;

        private void ChkAutoLoginOnBoot_Changed(object sender, RoutedEventArgs e)
        {
            Settings.AutoLoginOnBoot = ChkAutoLoginOnBoot.IsChecked == true;
            UpdatePreferredLabel();
        }

        private void ChkMinimizeOnClose_Changed(object sender, RoutedEventArgs e)
            => Settings.MinimizeOnClose = ChkMinimizeOnClose.IsChecked == true;

        private void ChkAutoUpdate_Changed(object sender, RoutedEventArgs e)
            => Settings.AutoUpdateCheck = ChkAutoUpdate.IsChecked == true;

        private async void CheckNow_Click(object sender, RoutedEventArgs e)
        {
            CheckNowBtn.IsEnabled = false;
            UpdateStatus.Text     = "Checking...";

            try
            {
                if (Owner is MainWindow main)
                {
                    var result = await main.CheckForUpdateAsync(ignoreThrottle: true);
                    UpdateStatus.Text = result switch
                    {
                        MainWindow.UpdateCheckResult.Newer    => "Update available — see banner.",
                        MainWindow.UpdateCheckResult.UpToDate => $"You're on the latest version (v{Updater.CurrentVersion()}).",
                        MainWindow.UpdateCheckResult.Failed   => "Couldn't reach GitHub.",
                        _                                     => "Update check skipped.",
                    };
                }
                else
                {
                    UpdateStatus.Text = "Open the main window first.";
                }
            }
            finally
            {
                CheckNowBtn.IsEnabled = true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void UpdatePreferredLabel()
        {
            if (!string.IsNullOrEmpty(Settings.PreferredAccount))
                LblPreferred.Text = $"Preferred account: {Settings.PreferredAccount}";
            else
                LblPreferred.Text =
                    "No preferred account — click the ☆ next to an account in the main window.";

            if (ChkAutoLoginOnBoot.IsChecked == true && !Settings.StartAtStartup)
                LblPreferred.Text +=
                    "\nEnable \"Start League Login when Windows starts\" for this to take effect.";
        }
    }
}