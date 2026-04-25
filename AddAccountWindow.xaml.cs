using System;
using System.Windows;
using LeagueLogin.Services;
using MessageBox = System.Windows.MessageBox;

namespace LeagueLogin
{
    public partial class AddAccountWindow : Window
    {
        private readonly string? _originalLabel;

        public AddAccountWindow()
        {
            InitializeComponent();
            _originalLabel  = null;
            TitleLabel.Text = "ADD  ACCOUNT";
            Loaded += (_, _) => NameBox.Focus();
        }

        public AddAccountWindow(string label, string username, string password)
        {
            InitializeComponent();
            _originalLabel  = label;
            TitleLabel.Text = "EDIT  ACCOUNT";
            Loaded += (_, _) =>
            {
                NameBox.Text     = label;
                UserBox.Text     = username;
                PassBox.Password = password;
                UserBox.Focus();
            };
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            var name = NameBox.Text.Trim();
            var user = UserBox.Text.Trim();
            var pass = PassBox.Password;

            if (string.IsNullOrEmpty(name)) { ShowError("Please enter a label."); return; }
            if (string.IsNullOrEmpty(user)) { ShowError("Please enter your Riot username or email."); return; }
            if (string.IsNullOrEmpty(pass)) { ShowError("Please enter your password."); return; }

            bool labelChanged = _originalLabel != null && name != _originalLabel;

            if (CredentialStore.AccountExists(name) && (_originalLabel == null || labelChanged))
            {
                var r = MessageBox.Show(
                    "An account called '" + name + "' already exists. Overwrite it?",
                    "Overwrite?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }

            if (labelChanged)
            {
                // Full rename: move the credential, move usage metadata, and
                // update the preferred-account pointer if this was it. Without
                // this, the old label leaves a ghost reference in Settings.
                CredentialStore.DeleteAccount(_originalLabel!);
                AccountMeta.Rename(_originalLabel!, name);
                if (string.Equals(Settings.PreferredAccount, _originalLabel,
                                   StringComparison.OrdinalIgnoreCase))
                    Settings.PreferredAccount = name;
            }

            CredentialStore.SaveAccount(name, user, pass);
            Logger.Write("Account saved: " + name);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void ShowError(string msg)
        {
            ErrorText.Text       = msg;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
