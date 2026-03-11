using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LeagueLogin.Services;

namespace LeagueLogin
{
    /// <summary>
    /// Owns the system-tray NotifyIcon and its context menu.
    /// Also builds the Windows taskbar Jump List.
    /// </summary>
    internal sealed class TrayManager : IDisposable
    {
        private const string GitHubUrl = "https://github.com/jduust/LeagueLogin";

        private readonly NotifyIcon _ni;
        private readonly Action<string> _onAccountClick;
        private readonly Action _onShowWindow;
        private readonly Action _onExit;
        private bool _disposed;

        public TrayManager(
            Action<string> onAccountClick,
            Action onShowWindow,
            Action onExit)
        {
            _onAccountClick = onAccountClick;
            _onShowWindow   = onShowWindow;
            _onExit         = onExit;

            _ni = new NotifyIcon
            {
                Icon    = BuildIcon(),
                Text    = "League Login",
                Visible = true,
            };

            _ni.DoubleClick += (_, _) => _onShowWindow();
        }

        // ── Public surface ────────────────────────────────────────────

        public void SetTooltip(string text)
        {
            // NotifyIcon.Text max is 63 chars
            _ni.Text = text.Length > 63 ? text[..63] : text;
        }

        public void ShowBalloon(string title, string message,
            ToolTipIcon icon = ToolTipIcon.Info, int ms = 2000)
        {
            _ni.ShowBalloonTip(ms, title, message, icon);
        }

        /// <summary>Rebuild context menu and jump list from current account list.</summary>
        public void Refresh(IReadOnlyList<string> accounts)
        {
            _ni.ContextMenuStrip?.Dispose();
            _ni.ContextMenuStrip = BuildContextMenu(accounts);
            BuildJumpList(accounts);
        }

        // ── Context menu ──────────────────────────────────────────────

        private ContextMenuStrip BuildContextMenu(IReadOnlyList<string> accounts)
        {
            var bg  = Color.FromArgb(0x14, 0x14, 0x2A);
            var fg  = Color.FromArgb(0xE8, 0xE0, 0xD0);
            var acc = Color.FromArgb(0xC8, 0x9B, 0x3C);
            var dim = Color.FromArgb(0x7A, 0x7A, 0x9A);

            var menu = new ContextMenuStrip { BackColor = bg, ForeColor = fg, RenderMode = ToolStripRenderMode.System };

            // Header item (non-clickable label)
            var header = new ToolStripMenuItem("LEAGUE  LOGIN") { ForeColor = acc, Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            if (accounts.Count == 0)
            {
                menu.Items.Add(new ToolStripMenuItem("No accounts saved") { Enabled = false });
            }
            else
            {
                foreach (var name in accounts)
                {
                    var capture = name;
                    var item = new ToolStripMenuItem(capture) { ForeColor = fg };
                    item.Click += (_, _) => _onAccountClick(capture);
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new ToolStripSeparator());

            var show = new ToolStripMenuItem("Open Window");
            show.Click += (_, _) => _onShowWindow();
            menu.Items.Add(show);

            var logs = new ToolStripMenuItem("View Logs");
            logs.Click += (_, _) => OpenLogs();
            menu.Items.Add(logs);

            var github = new ToolStripMenuItem("GitHub (Updates)");
            github.Click += (_, _) =>
                Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true });
            menu.Items.Add(github);

            menu.Items.Add(new ToolStripSeparator());

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (_, _) => _onExit();
            menu.Items.Add(exit);

            return menu;
        }

        // ── Jump List ─────────────────────────────────────────────────

        private static void BuildJumpList(IReadOnlyList<string> accounts)
        {
            try
            {
                var exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? string.Empty;

                var jl = new System.Windows.Shell.JumpList();

                foreach (var name in accounts)
                {
                    jl.JumpItems.Add(new System.Windows.Shell.JumpTask
                    {
                        Title           = name,
                        Description     = $"Launch League of Legends as {name}",
                        ApplicationPath = exePath,
                        Arguments       = $"--account \"{name}\"",
                        CustomCategory  = "Accounts",
                    });
                }

                jl.ShowFrequentCategory = false;
                jl.ShowRecentCategory   = false;
                System.Windows.Shell.JumpList.SetJumpList(
                    System.Windows.Application.Current, jl);
            }
            catch (Exception ex)
            {
                Logger.WriteException("JumpList build", ex);
            }
        }

        // ── Icon (generated at runtime — no .ico file required) ───────

        private static Icon BuildIcon()
        {
            // Try the exe's own icon first
            try
            {
                var exe = Environment.ProcessPath ?? string.Empty;
                if (File.Exists(exe))
                {
                    var ico = Icon.ExtractAssociatedIcon(exe);
                    if (ico != null) return ico;
                }
            }
            catch { }

            // Fallback: draw a small gold circle with "L"
            try
            {
                using var bmp = new Bitmap(32, 32);
                using var g   = Graphics.FromImage(bmp);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var bgBrush = new SolidBrush(Color.FromArgb(0xC8, 0x9B, 0x3C));
                g.FillEllipse(bgBrush, 1, 1, 30, 30);
                using var fgBrush = new SolidBrush(Color.FromArgb(0x0D, 0x0D, 0x1A));
                using var font    = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString("L", font, fgBrush, 7f, 5f);
                var hIcon = bmp.GetHicon();
                var icon  = (Icon)Icon.FromHandle(hIcon).Clone();
                DestroyIcon(hIcon);
                return icon;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        // ── Helpers ───────────────────────────────────────────────────

        private static void OpenLogs()
        {
            try
            {
                if (File.Exists(Logger.LogPath))
                    Process.Start(new ProcessStartInfo(Logger.LogPath) { UseShellExecute = true });
                else
                    MessageBox.Show("No log file found yet.", "League Login",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        // ── IDisposable ───────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ni.ContextMenuStrip?.Dispose();
            _ni.Dispose();
        }
    }
}
