using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using LeagueLogin.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace LeagueLogin
{
    public partial class MainWindow : Window
    {
        private const double TitleBarHeight      = 34;
        private const double ContentMargins      = 40;
        private const double HeaderHeight        = 30;
        private const double SeparatorHeight     = 5;
        private const double StatusBarHeight     = 44;
        private const double AccountRowPx        = 70;
        private const double EmptyStateHeight    = 110;
        private const double ScreenPadding       = 56;
        // Window stops growing after this many accounts; scrollbar takes over
        private const int    MaxVisibleAccounts  = 5;

        private bool        _busy;
        private TrayManager _tray = null!;

        // P/Invoke
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        private const int  SW_RESTORE            = 9;
        private const byte VK_MENU               = 0x12;
        private const uint KEYEVENTF_KEYUP        = 0x0002;
        private const uint KEYEVENTF_EXTENDEDKEY  = 0x0001;

        public MainWindow()
        {
            InitializeComponent();
            _tray = new TrayManager(
                onAccountClick: async name => await LaunchWithAccount(name),
                onShowWindow:   BringToFront,
                onExit:         ExitApp);
            Loaded += (_, _) => RefreshAccounts();
        }

        private void BringToFront()
        {
            Dispatcher.Invoke(() =>
            {
                // 1. Let WPF know the window should be visible
                Show();
                WindowState = WindowState.Normal;

                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    // 2. Win32 SW_RESTORE: properly un-hides the window from any
                    //    state. WPF's Show() alone can leave it in a
                    //    minimized-but-visible-in-taskbar limbo when called on a
                    //    window that was hidden while its state was still Normal.
                    ShowWindow(hwnd, SW_RESTORE);

                    // 3. Simulate Alt to grant this thread foreground permission.
                    //    Without it, SetForegroundWindow silently fails from a
                    //    WinForms tray-icon thread.
                    keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY, 0);
                    keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);

                    // 4. Bring to front
                    SetForegroundWindow(hwnd);
                }

                Activate();
            });
        }

        private void MinimizeToTray()
        {
            Hide();
            _tray.ShowBalloon("League Login",
                "Running in the background - right-click the tray icon.",
                WinForms.ToolTipIcon.Info, 2000);
        }

        private void ExitApp()
        {
            _tray.Dispose();
            Application.Current.Shutdown();
        }

        // ── Account list ──────────────────────────────────────────────

        private void RefreshAccounts()
        {
            var rows = AccountPanel.Children.OfType<UIElement>()
                .Where(e => e != EmptyLabel).ToList();
            foreach (var e in rows)
                AccountPanel.Children.Remove(e);

            var accounts = CredentialStore.ListAccounts();

            if (accounts.Count == 0)
                EmptyLabel.Visibility = Visibility.Visible;
            else
            {
                EmptyLabel.Visibility = Visibility.Collapsed;
                foreach (var name in accounts)
                    AccountPanel.Children.Add(BuildAccountRow(name));
            }

            _tray.Refresh(accounts);
            AutoSizeWindow(accounts.Count);
        }

        private UIElement BuildAccountRow(string accountName)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var btn = new Button { Style = (Style)FindResource("AccountButtonStyle") };
            btn.Click += async (_, _) => await LaunchWithAccount(accountName);
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock
            {
                Text       = accountName,
                FontSize   = 14,
                FontWeight = FontWeights.SemiBold,
            });
            inner.Children.Add(new TextBlock
            {
                Text       = "Click to launch",
                FontSize   = 11,
                Foreground = (SolidColorBrush)FindResource("TextMuted"),
                Margin     = new Thickness(0, 2, 0, 0),
            });
            btn.Content = inner;
            Grid.SetColumn(btn, 0);
            row.Children.Add(btn);

            var edit = new Button
            {
                Style             = (Style)FindResource("IconButtonStyle"),
                Content           = "Edit",
                FontSize          = 11,
                ToolTip           = "Edit " + accountName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(6, 0, 0, 0),
            };
            edit.Click += (_, _) =>
            {
                var cred   = CredentialStore.GetCredential(accountName);
                var dialog = new AddAccountWindow(
                    accountName, cred?.Username ?? "", cred?.Password ?? "")
                    { Owner = this };
                if (dialog.ShowDialog() == true)
                    RefreshAccounts();
            };
            Grid.SetColumn(edit, 1);
            row.Children.Add(edit);

            var del = new Button
            {
                Style             = (Style)FindResource("IconButtonStyle"),
                Content           = "Del",
                FontSize          = 11,
                ToolTip           = "Remove " + accountName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(4, 0, 0, 0),
            };
            del.Click += (_, _) =>
            {
                if (MessageBox.Show(
                        "Remove '" + accountName + "'?\nThis only removes it from this app - your Riot account is unaffected.",
                        "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Question)
                    == MessageBoxResult.Yes)
                {
                    CredentialStore.DeleteAccount(accountName);
                    RefreshAccounts();
                }
            };
            Grid.SetColumn(del, 2);
            row.Children.Add(del);

            return row;
        }

        private void AutoSizeWindow(int accountCount)
        {
            // Cap the window at MaxVisibleAccounts rows; beyond that the
            // ScrollViewer handles overflow with the themed scrollbar.
            int    visibleRows = Math.Min(accountCount, MaxVisibleAccounts);
            double listHeight  = accountCount == 0 ? EmptyStateHeight : visibleRows * AccountRowPx;
            double ideal       = TitleBarHeight + ContentMargins + HeaderHeight
                               + SeparatorHeight + 16 + listHeight + StatusBarHeight;

            double screenMax   = SystemParameters.WorkArea.Height - ScreenPadding;
            Height = Math.Min(Math.Max(ideal, MinHeight), screenMax);
        }

        // ── Add account ───────────────────────────────────────────────

        private void AddAccount_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddAccountWindow { Owner = this };
            if (dialog.ShowDialog() == true)
                RefreshAccounts();
        }

        // ── Launch flow ───────────────────────────────────────────────

        public async Task LaunchWithAccount(string accountName)
        {
            if (_busy) return;
            _busy = true;
            SetBusy(true, "Reading credentials...");
            Logger.Write("Launching account: " + accountName);

            try
            {
                var cred = CredentialStore.GetCredential(accountName);
                if (cred == null)
                {
                    SetStatus("Credentials not found for '" + accountName + "'");
                    return;
                }

                SetStatus("Stopping existing Riot / League processes...");
                RiotAutomation.KillLeagueProcesses();

                SetStatus("Starting Riot Client...");
                if (!RiotAutomation.LaunchRiotClient())
                {
                    SetStatus("Riot Client not found - check View Logs for details");
                    Logger.Write("LaunchRiotClient returned false");
                    return;
                }

                SetStatus("Waiting for login screen...");
                bool ok = await Task.Run(() =>
                    RiotAutomation.WaitAndFillLogin(
                        cred.Value.Username, cred.Value.Password, Logger.Write));

                if (ok)
                {
                    Logger.Write("Login succeeded for: " + accountName);
                    SetStatus("Logged in as " + accountName);
                    await Task.Delay(600);
                    MinimizeToTray();
                }
                else
                {
                    Logger.Write("Login timed out");
                    SetStatus("Login fields not found within timeout - check View Logs");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteException("LaunchWithAccount", ex);
                SetStatus("Error: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
                _busy = false;
            }
        }

        // ── Custom chrome handlers ────────────────────────────────────

        private void WinMin_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void WinMaxRestore_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;

        private void WinClose_Click(object sender, RoutedEventArgs e) => ExitApp();

        private void Window_StateChanged(object sender, EventArgs e)
        {
            // Redirect taskbar minimize to tray
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                WindowState = WindowState.Normal;
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            RootGrid.Margin       = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
            MaxRestoreBtn.Content = WindowState == WindowState.Maximized ? "[ ]" : "[ ]";
            MaxRestoreBtn.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            ExitApp();
        }

        // ── Status helpers ────────────────────────────────────────────

        private void SetStatus(string message)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = message;
                _tray.SetTooltip("League Login - " + message);
            });
        }

        private void SetBusy(bool busy, string? message = null)
        {
            Dispatcher.Invoke(() =>
            {
                Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                if (message != null) SetStatus(message);
                foreach (var row in AccountPanel.Children.OfType<Grid>())
                    foreach (var btn in row.Children.OfType<Button>())
                        btn.IsEnabled = !busy;
            });
        }
    }
}
