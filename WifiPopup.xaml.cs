using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace TopBar
{
    /// <summary>
    /// macOS Wi-Fi menu equivalent: on/off switch, live network scan with
    /// signal strength, one-click connect (open / known), password prompt for
    /// new secured networks, disconnect + settings link.
    /// </summary>
    public partial class WifiPopup : Window
    {
        private bool _ready, _closing, _busy;
        private DispatcherTimer? _leaveTimer;
        private Radio? _radio;
        private WifiNetwork? _pendingSecured;

        public WifiPopup()
        {
            InitializeComponent();
            Loaded += (_, _) => MenuChrome.Apply(this);
            ContentRendered += (_, _) => _ready = true;
            PasswordBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) _ = PasswordConnectAsync();
                if (e.Key == Key.Escape) PasswordPanel.Visibility = Visibility.Collapsed;
            };

            InitAsync();
        }

        private async void InitAsync()
        {
            bool wifiOn = true;
            try
            {
                var radios = await Radio.GetRadiosAsync();
                _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                wifiOn = _radio == null || _radio.State == RadioState.On;
            }
            catch { }
            Dispatcher.Invoke(() => WifiText.Text = _radio == null ? "N/A" : (wifiOn ? "On" : "Off"));
            if (!wifiOn)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Wi-Fi is off — turn it on to see networks.";
                    NetworkHost.Children.Clear();
                });
                return;
            }
            await RefreshAsync();
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Scanning…";
                NetworkHost.Children.Clear();
            });
            var networks = await WifiNative.ScanAsync();
            var status = await WifiNative.GetStatusAsync();
            Dispatcher.Invoke(() => Render(networks, status));
            _busy = false;
        }

        private void Render(List<WifiNetwork> networks, (bool Ok, string CurrentSsid, uint Signal) status)
        {
            NetworkHost.Children.Clear();
            PasswordPanel.Visibility = Visibility.Collapsed;
            FooterHost.Children.Clear();

            if (!networks.Any())
            {
                StatusText.Text = status.Ok ? "No networks found." : "No Wi-Fi adapter found.";
            }
            else if (!string.IsNullOrEmpty(status.CurrentSsid))
            {
                StatusText.Text = $"Connected to {status.CurrentSsid}  ({status.Signal}%)";
            }
            else
            {
                StatusText.Text = "Not connected — pick a network:";
            }

            foreach (var n in networks.Take(20))
            {
                string hint = n.Connected ? $"✓ {n.Signal}%"
                    : n.Secured ? $"{n.Signal}% · Secured" : $"{n.Signal}% · Open";
                var row = MenuChrome.MenuRow(n.Ssid, hint, () => _ = JoinAsync(n));
                if (n.Connected)
                    row.Background = Application.Current.Resources["PopupAccent"] as SolidColorBrush
                        ?? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                NetworkHost.Children.Add(row);
            }

            if (!string.IsNullOrEmpty(status.CurrentSsid))
                FooterHost.Children.Add(MenuChrome.MenuRow("Disconnect", "", () => _ = DisconnectAsync()));
            FooterHost.Children.Add(MenuChrome.MenuRow("Network settings…", "", () =>
            {
                SafeClose();
                ForegroundActions.OpenSettings("network-wifi");
            }));
        }

        private async System.Threading.Tasks.Task JoinAsync(WifiNetwork n)
        {
            if (_busy || n.Connected) return;
            _busy = true;
            StatusText.Text = $"Connecting to {n.Ssid}…";
            PasswordPanel.Visibility = Visibility.Collapsed;

            bool ok;
            if (!n.Secured)
            {
                ok = await WifiNative.ConnectOpenAsync(n.Ssid);
            }
            else
            {
                // Stored profile first (silent for known networks)…
                ok = await WifiNative.TryKnownProfileAsync(n.Ssid);
                if (!ok)
                {
                    // …otherwise ask for the password.
                    _pendingSecured = n;
                    PasswordSsid.Text = n.Ssid;
                    PasswordBox.Password = "";
                    PasswordError.Visibility = Visibility.Collapsed;
                    PasswordPanel.Visibility = Visibility.Visible;
                    PasswordBox.Focus();
                    _busy = false;
                    return;
                }
            }

            StatusText.Text = ok ? "Connected." : "Could not connect.";
            _busy = false;
            await RefreshAsync();
        }

        private async System.Threading.Tasks.Task PasswordConnectAsync()
        {
            if (_pendingSecured == null || _busy) return;
            var n = _pendingSecured;
            string pw = PasswordBox.Password;
            if (string.IsNullOrEmpty(pw))
            {
                PasswordError.Text = "Enter the network password.";
                PasswordError.Visibility = Visibility.Visible;
                return;
            }
            _busy = true;
            StatusText.Text = $"Connecting to {n.Ssid}…";
            bool ok = await WifiNative.ConnectSecuredAsync(n.Ssid, n.AuthAlgo, n.CipherAlgo, pw);
            if (!ok)
            {
                PasswordError.Text = "Could not connect — check the password.";
                PasswordError.Visibility = Visibility.Visible;
                StatusText.Text = "Connection failed.";
            }
            else
            {
                _pendingSecured = null;
            }
            _busy = false;
            await RefreshAsync();
        }

        private void PasswordConnect_Click(object sender, MouseButtonEventArgs e) => _ = PasswordConnectAsync();

        private void PasswordCancel_Click(object sender, MouseButtonEventArgs e)
        {
            _pendingSecured = null;
            PasswordPanel.Visibility = Visibility.Collapsed;
        }

        private async System.Threading.Tasks.Task DisconnectAsync()
        {
            await WifiNative.DisconnectAsync();
            await RefreshAsync();
        }

        private async void WifiToggle_Click(object sender, MouseButtonEventArgs e)
        {
            if (_radio == null) return;
            try
            {
                bool on = _radio.State == RadioState.On;
                var res = await _radio.SetStateAsync(on ? RadioState.Off : RadioState.On);
                if (res == RadioAccessStatus.Allowed)
                {
                    WifiText.Text = on ? "Off" : "On";
                    await RefreshAsync();
                }
            }
            catch { }
        }

        private async void Rescan_Click(object sender, MouseButtonEventArgs e) => await RefreshAsync();

        // ── Close logic ─────────────────────────────────────────────────────
        private void Window_Deactivated(object sender, EventArgs e) { if (_ready) SafeClose(); }
        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (PasswordPanel.IsVisible) return; // don't vanish mid-typing
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
