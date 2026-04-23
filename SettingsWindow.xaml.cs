using System.Windows;
using LeagueLogin.Services;

namespace LeagueLogin
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            // Load current values — set IsChecked without firing the Changed events
            ChkStartup.IsChecked         = Settings.StartAtStartup;
            ChkStartMinimized.IsChecked  = Settings.StartMinimized;
            ChkMinimizeOnClose.IsChecked = Settings.MinimizeOnClose;
        }

        private void ChkStartup_Changed(object sender, RoutedEventArgs e)
            => Settings.StartAtStartup = ChkStartup.IsChecked == true;

        private void ChkStartMinimized_Changed(object sender, RoutedEventArgs e)
            => Settings.StartMinimized = ChkStartMinimized.IsChecked == true;

        private void ChkMinimizeOnClose_Changed(object sender, RoutedEventArgs e)
            => Settings.MinimizeOnClose = ChkMinimizeOnClose.IsChecked == true;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}