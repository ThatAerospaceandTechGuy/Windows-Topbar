using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopBar
{
    /// <summary>
    /// macOS Apple-menu equivalent: About This PC, System Settings, App Store,
    /// TopBar Settings, Recent Items, Force Quit, Sleep / Restart / Shut Down /
    /// Lock / Log Out.
    /// </summary>
    public partial class AppleMenuPopup : Window
    {
        private bool _ready, _closing;
        private DispatcherTimer? _leaveTimer;
        private readonly Action _openTopBarSettings;

        public AppleMenuPopup(Action openTopBarSettings)
        {
            InitializeComponent();
            Loaded += (_, _) => MenuChrome.Apply(this);
            ContentRendered += (_, _) => _ready = true;
            _openTopBarSettings = openTopBarSettings;

            BuildActions();
            BuildRecent();
            BuildPower();
            LoadAboutAsync();
        }

        private void AddItem(Panel host, string label, string hint, Action action)
        {
            host.Children.Add(MenuChrome.MenuRow(label, hint, () =>
            {
                SafeClose();
                try { action(); } catch { }
            }));
        }

        private void BuildActions()
        {
            AddItem(ActionHost, "About This PC", "", () =>
                ForegroundActions.OpenSettings("about"));
            AddItem(ActionHost, "System Settings…", "", () =>
                ForegroundActions.OpenSettings(""));
            AddItem(ActionHost, "App Store…", "", ForegroundActions.OpenStore);
            AddItem(ActionHost, "TopBar Settings…", "", () => _openTopBarSettings());
            AddItem(ActionHost, "Force Quit…", "Task Manager", ForegroundActions.ForceQuit);
        }

        private void BuildRecent()
        {
            var items = ForegroundActions.RecentItems();
            if (items.Count == 0)
            {
                RecentHost.Children.Add(new TextBlock
                {
                    Text = "No recent items",
                    Foreground = Application.Current.Resources["PopupFgDim"] as SolidColorBrush
                        ?? new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                    FontSize = 12,
                    Margin = new Thickness(8, 0, 8, 0)
                });
                return;
            }
            foreach (var (name, path) in items)
            {
                var row = MenuChrome.MenuRow(name, "", () =>
                {
                    SafeClose();
                    ForegroundActions.OpenPath(path);
                });
                // Keep long file names on one line with ellipsis
                if (row.Child is Grid g && g.Children[0] is TextBlock tb)
                    tb.TextTrimming = TextTrimming.CharacterEllipsis;
                RecentHost.Children.Add(row);
            }
        }

        private void BuildPower()
        {
            AddItem(PowerHost, "Sleep", "", ForegroundActions.Sleep);
            AddItem(PowerHost, "Restart…", "", ForegroundActions.Restart);
            AddItem(PowerHost, "Shut Down…", "", ForegroundActions.ShutDown);
            AddItem(PowerHost, "Lock Screen", "", ForegroundActions.Lock);
            AddItem(PowerHost, "Log Out…", "", ForegroundActions.LogOut);
        }

        private async void LoadAboutAsync()
        {
            try
            {
                var text = await System.Threading.Tasks.Task.Run(() =>
                {
                    string os = RuntimeInformation.OSDescription.Trim();
                    string cpu = "", ram = "";
                    try
                    {
                        using var s = new System.Management.ManagementObjectSearcher(
                            "SELECT Name FROM Win32_Processor");
                        foreach (System.Management.ManagementObject o in s.Get())
                        {
                            cpu = (o["Name"]?.ToString() ?? "").Trim();
                            break;
                        }
                    }
                    catch { }
                    try
                    {
                        var (total, _, _) = SystemStats.GetMemory();
                        if (total > 0) ram = SystemStats.FormatBytes(total) + " RAM";
                    }
                    catch { }
                    return string.Join("\n", new[] { os, cpu, ram });
                });
                Dispatcher.Invoke(() => AboutText.Text = text);
            }
            catch { Dispatcher.Invoke(() => AboutText.Text = RuntimeInformation.OSDescription); }
        }

        // ── Close logic ─────────────────────────────────────────────────────
        private void Window_Deactivated(object sender, EventArgs e) { if (_ready) SafeClose(); }
        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_leaveTimer == null)
            {
                _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); SafeClose(); };
            }
            _leaveTimer.Start();
        }
        private void Window_MouseEnter(object sender, MouseEventArgs e) { _leaveTimer?.Stop(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.Key == Key.Escape) SafeClose(); base.OnKeyDown(e); }
        private void SafeClose() { if (_closing) return; _closing = true; _leaveTimer?.Stop(); Close(); }
    }
}
