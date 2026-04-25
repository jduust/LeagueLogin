using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using LeagueLogin.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;
using Orientation = System.Windows.Controls.Orientation;

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
        private const double UpdateBannerHeight  = 95;
        // Window stops growing after this many accounts; scrollbar takes over
        private const int    MaxVisibleAccounts  = 5;

        private bool        _busy;
        private TrayManager _tray = null!;
        private UpdateInfo? _pendingUpdate;
        private bool        _updateBannerVisible;

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
            Loaded += async (_, _) =>
            {
                RefreshAccounts();
                // Startup update check, throttled by Settings.LastUpdateCheckUtc.
                await CheckForUpdateAsync(ignoreThrottle: false);
            };
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

            var accounts = SortAccounts(CredentialStore.ListAccounts());

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

        /// <summary>
        /// Orders accounts so the most useful ones float to the top:
        ///   1. Preferred account first (the boot-login target).
        ///   2. Accounts with a LastUsedUtc, most-recent first.
        ///   3. Never-used accounts, alphabetically.
        /// </summary>
        private static List<string> SortAccounts(IEnumerable<string> raw)
        {
            var pref = Services.Settings.PreferredAccount;
            return raw
                .OrderByDescending(a => string.Equals(a, pref, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(a => AccountMeta.Get(a).LastUsedUtc ?? DateTime.MinValue)
                .ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private UIElement BuildAccountRow(string accountName)
        {
            bool isPreferred = string.Equals(
                Services.Settings.PreferredAccount, accountName,
                StringComparison.OrdinalIgnoreCase);
            var meta = AccountMeta.Get(accountName);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 0 main
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 1 star
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 2 overflow

            // ── Main launch button (with inline header + usage subtitle) ──
            // WPF opens btn.ContextMenu on right-click by default — no manual
            // MouseRightButton handling needed.
            var btn = new Button { Style = (Style)FindResource("AccountButtonStyle") };
            btn.Click += async (_, _) => await LaunchWithAccount(accountName);
            btn.ContextMenu = BuildRowContextMenu(accountName, isPreferred);

            var inner = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text       = accountName,
                FontSize   = 14,
                FontWeight = FontWeights.SemiBold,
            });
            if (isPreferred)
            {
                header.Children.Add(new TextBlock
                {
                    Text              = "  ★ preferred",
                    FontSize          = 10,
                    Foreground        = (SolidColorBrush)FindResource("Accent"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            inner.Children.Add(header);
            inner.Children.Add(new TextBlock
            {
                Text       = BuildSubtitle(meta),
                FontSize   = 11,
                Foreground = (SolidColorBrush)FindResource("TextMuted"),
                Margin     = new Thickness(0, 2, 0, 0),
            });
            btn.Content = inner;
            Grid.SetColumn(btn, 0);
            row.Children.Add(btn);

            // ── Star toggle (preferred account) ───────────────────────────
            var star = new Button
            {
                Style             = (Style)FindResource("IconButtonStyle"),
                Content           = isPreferred ? "★" : "☆",
                FontSize          = 16,
                Foreground        = isPreferred ? (SolidColorBrush)FindResource("Accent")
                                                : (SolidColorBrush)FindResource("TextMuted"),
                ToolTip           = isPreferred
                    ? "Preferred account (click to unset)"
                    : "Set as preferred account for boot auto-login",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(6, 0, 0, 0),
            };
            star.Click += (_, _) => TogglePreferred(accountName, isPreferred);
            Grid.SetColumn(star, 1);
            row.Children.Add(star);

            // ── Overflow menu (⋯) — Edit / Remove / Copy username ─────────
            var overflow = new Button
            {
                Style             = (Style)FindResource("IconButtonStyle"),
                Content           = "⋯",
                FontSize          = 16,
                ToolTip           = "More",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(4, 0, 0, 0),
            };
            var menu = BuildRowContextMenu(accountName, isPreferred);
            overflow.Click += (_, _) =>
            {
                menu.PlacementTarget = overflow;
                menu.IsOpen = true;
            };
            Grid.SetColumn(overflow, 2);
            row.Children.Add(overflow);

            return row;
        }

        private static string BuildSubtitle(AccountMeta.Entry meta)
        {
            if (meta.LastUsedUtc is DateTime t)
            {
                var count = meta.LaunchCount;
                var rel   = AccountMeta.Relative(t);
                return count > 1
                    ? $"Launched {rel} · {count} total"
                    : $"Launched {rel}";
            }
            return "Click to launch";
        }

        private void TogglePreferred(string accountName, bool isCurrentlyPreferred)
        {
            Services.Settings.PreferredAccount = isCurrentlyPreferred ? null : accountName;
            RefreshAccounts();
        }

        private ContextMenu BuildRowContextMenu(string accountName, bool isPreferred)
        {
            var menu = new ContextMenu();

            var launch = new MenuItem { Header = "Launch" };
            launch.Click += async (_, _) => await LaunchWithAccount(accountName);
            menu.Items.Add(launch);

            var copy = new MenuItem { Header = "Copy username" };
            copy.Click += (_, _) =>
            {
                var cred = CredentialStore.GetCredential(accountName);
                if (cred is { } c && !string.IsNullOrEmpty(c.Username))
                {
                    try { Clipboard.SetText(c.Username); SetStatus("Username copied."); }
                    catch (Exception ex) { SetStatus("Copy failed: " + ex.Message); }
                }
            };
            menu.Items.Add(copy);

            menu.Items.Add(new Separator());

            var prefer = new MenuItem
            {
                Header = isPreferred ? "Unset preferred" : "Set as preferred",
            };
            prefer.Click += (_, _) => TogglePreferred(accountName, isPreferred);
            menu.Items.Add(prefer);

            var edit = new MenuItem { Header = "Edit…" };
            edit.Click += (_, _) =>
            {
                var cred   = CredentialStore.GetCredential(accountName);
                var dialog = new AddAccountWindow(
                    accountName, cred?.Username ?? "", cred?.Password ?? "")
                    { Owner = this };
                if (dialog.ShowDialog() == true)
                    RefreshAccounts();
            };
            menu.Items.Add(edit);

            menu.Items.Add(new Separator());

            var remove = new MenuItem { Header = "Remove…" };
            remove.Click += (_, _) => RemoveAccount(accountName, isPreferred);
            menu.Items.Add(remove);

            return menu;
        }

        private void RemoveAccount(string accountName, bool isPreferred)
        {
            if (MessageBox.Show(
                    $"Remove '{accountName}'?\nThis only removes it from this app - your Riot account is unaffected.",
                    "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;

            if (isPreferred) Services.Settings.PreferredAccount = null;
            CredentialStore.DeleteAccount(accountName);
            AccountMeta.Remove(accountName);
            RefreshAccounts();
        }

        private void AutoSizeWindow(int accountCount)
        {
            // Cap the window at MaxVisibleAccounts rows; beyond that the
            // ScrollViewer handles overflow with the themed scrollbar.
            int    visibleRows = Math.Min(accountCount, MaxVisibleAccounts);
            double listHeight  = accountCount == 0 ? EmptyStateHeight : visibleRows * AccountRowPx;
            double bannerH     = _updateBannerVisible ? UpdateBannerHeight : 0;
            double ideal       = TitleBarHeight + ContentMargins + HeaderHeight
                               + SeparatorHeight + 16 + listHeight + StatusBarHeight
                               + bannerH;

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
                    AccountMeta.RecordLaunch(accountName);
                    SetStatus("Logged in as " + accountName);
                    await Task.Delay(600);
                    MinimizeToTray();
                    await Task.Run(() => RiotAutomation.WaitAndClickPlay(Logger.Write));
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

        private void WinClose_Click(object sender, RoutedEventArgs e)
        {
            if (Services.Settings.MinimizeOnClose)
                MinimizeToTray();
            else
                ExitApp();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            if (Services.Settings.MinimizeOnClose)
                MinimizeToTray();
            else
                ExitApp();
        }

        private void WinSettings_Click(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow { Owner = this };
            w.ShowDialog();
}
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
            MaxRestoreBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            MaxRestoreBtn.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }
        
        // ── Updater ───────────────────────────────────────────────────

        public enum UpdateCheckResult { Skipped, UpToDate, Newer, Failed }

        /// <summary>
        /// Hits the GitHub API. Honors the user's auto-update preference and the
        /// once-per-day throttle unless ignoreThrottle is true (Settings dialog
        /// "Check now" passes true). If a newer version is available and not on
        /// the user's skip list, populates the banner.
        /// </summary>
        public async Task<UpdateCheckResult> CheckForUpdateAsync(bool ignoreThrottle)
        {
            if (!ignoreThrottle)
            {
                if (!Services.Settings.AutoUpdateCheck) return UpdateCheckResult.Skipped;
                if (Services.Settings.LastUpdateCheckUtc is DateTime last &&
                    (DateTime.UtcNow - last).TotalHours < 24)
                    return UpdateCheckResult.Skipped;
            }

            var info = await Updater.CheckAsync();
            Services.Settings.LastUpdateCheckUtc = DateTime.UtcNow;

            if (info == null) return UpdateCheckResult.Failed;
            if (!Updater.IsNewer(info)) return UpdateCheckResult.UpToDate;

            // Manual checks bypass the user's "Skip this version" choice — they
            // explicitly asked, so show the banner regardless.
            if (!ignoreThrottle &&
                string.Equals(Services.Settings.SkippedVersion, info.Version.ToString(),
                              StringComparison.OrdinalIgnoreCase))
                return UpdateCheckResult.UpToDate;

            ShowUpdateBanner(info);
            _tray.ShowBalloon("League Login",
                $"Update available: v{info.Version}",
                WinForms.ToolTipIcon.Info, 3500);
            return UpdateCheckResult.Newer;
        }

        private void ShowUpdateBanner(UpdateInfo info)
        {
            Dispatcher.Invoke(() =>
            {
                _pendingUpdate = info;
                UpdateBannerTitle.Text =
                    $"Update available: v{info.Version} (you have v{Updater.CurrentVersion()})";
                UpdateBannerSubtitle.Text = string.IsNullOrWhiteSpace(info.Title)
                    ? "Click Release notes to see what changed."
                    : info.Title;
                UpdateInstallText.Text = "Download & install";
                UpdateInstallBtn.IsEnabled = !string.IsNullOrEmpty(info.MsiAssetUrl);
                if (!UpdateInstallBtn.IsEnabled)
                    UpdateInstallBtn.ToolTip = "This release has no MSI asset — use Release notes to download manually.";

                UpdateBanner.Visibility = Visibility.Visible;
                _updateBannerVisible = true;
                AutoSizeWindow(CredentialStore.ListAccounts().Count);
            });
        }

        private void HideUpdateBanner()
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            _updateBannerVisible = false;
            AutoSizeWindow(CredentialStore.ListAccounts().Count);
        }

        private void UpdateNotes_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate != null) Updater.OpenReleaseInBrowser(_pendingUpdate);
        }

        private void UpdateSkip_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate != null)
                Services.Settings.SkippedVersion = _pendingUpdate.Version.ToString();
            HideUpdateBanner();
        }

        private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate == null) return;
            var info = _pendingUpdate;

            UpdateInstallBtn.IsEnabled = false;
            UpdateSkipBtn.IsEnabled    = false;
            UpdateNotesBtn.IsEnabled   = false;

            var progress = new Progress<double>(p =>
            {
                UpdateInstallText.Text = $"Downloading… {(int)(p * 100)}%";
            });

            string? msiPath = await Task.Run(() => Updater.DownloadAsync(info, progress));
            if (msiPath == null)
            {
                UpdateInstallText.Text = "Download failed — retry";
                UpdateInstallBtn.IsEnabled = true;
                UpdateSkipBtn.IsEnabled    = true;
                UpdateNotesBtn.IsEnabled   = true;
                return;
            }

            UpdateInstallText.Text = "Launching installer…";
            // Tell the user what's about to happen so the app exit isn't a surprise.
            SetStatus("Installer launching — League Login will close.");
            await Task.Delay(400);
            Updater.RunInstallerAndExit(msiPath);
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
