using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace TopBar
{
    /// <summary>
    /// macOS Bluetooth menu equivalent: on/off switch, paired-device list
    /// with connection status, tap to wake/connect, remove pairing, and an
    /// entry point to pair new devices.
    /// </summary>
    public partial class BluetoothPopup : Window
    {
        private bool _ready, _closing, _busy;
        private DispatcherTimer? _leaveTimer;
        private Radio? _radio;
        private bool _btEnabled;

        public BluetoothPopup()
        {
            InitializeComponent();
            Loaded += (_, _) => MenuChrome.Apply(this);
            ContentRendered += (_, _) => _ready = true;

            InitAsync();
        }

        private async void InitAsync()
        {
            try
            {
                var radios = await Radio.GetRadiosAsync();
                _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                _btEnabled = _radio?.State == RadioState.On;
            }
            catch { _btEnabled = false; }
            Dispatcher.Invoke(() =>
            {
                UpdateToggle();
                _ = RefreshDevicesAsync();
            });
        }

        private void UpdateToggle()
        {
            StatusText.Text = _btEnabled ? "On" : "Off";
            ToggleText.Text = _btEnabled ? "Turn Off" : "Turn On";
        }

        private async void Toggle_Click(object sender, MouseButtonEventArgs e)
        {
            if (_radio == null) return;
            try
            {
                var res = await _radio.SetStateAsync(_btEnabled ? RadioState.Off : RadioState.On);
                if (res == RadioAccessStatus.Allowed)
                {
                    _btEnabled = !_btEnabled;
                    UpdateToggle();
                    await RefreshDevicesAsync();
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task RefreshDevicesAsync()
        {
            if (_busy) return;
            _busy = true;
            Dispatcher.Invoke(() => DeviceHost.Children.Clear());

            var devices = new List<(DeviceInformation Info, string Name, bool Connected)>();
            if (_btEnabled)
            {
                try
                {
                    var found = await DeviceInformation.FindAllAsync(
                        BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                        new[] { "System.Devices.Aep.IsConnected" });
                    foreach (var d in found)
                    {
                        bool connected = false;
                        try
                        {
                            if (d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out object? v) && v is bool b)
                                connected = b;
                        }
                        catch { }
                        devices.Add((d, string.IsNullOrWhiteSpace(d.Name) ? "Unknown device" : d.Name, connected));
                    }
                }
                catch { }
            }

            Dispatcher.Invoke(() => Render(devices));
            _busy = false;
        }

        private void Render(List<(DeviceInformation Info, string Name, bool Connected)> devices)
        {
            DeviceHost.Children.Clear();
            FooterHost.Children.Clear();

            if (!_btEnabled)
            {
                StatusText.Text = "Bluetooth is off — turn it on to see devices.";
            }
            else if (devices.Count == 0)
            {
                StatusText.Text = "No paired devices.";
            }
            else
            {
                StatusText.Text = "Tap a device to wake / connect it:";
            }

            var hoverBg = Application.Current.Resources["PopupHoverBg"] as SolidColorBrush
                ?? new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            var fg = Application.Current.Resources["PopupFgBright"] as SolidColorBrush
                ?? new SolidColorBrush(Colors.White);
            var dim = Application.Current.Resources["PopupFgLabel"] as SolidColorBrush
                ?? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

            foreach (var (info, name, connected) in devices.OrderByDescending(d => d.Connected).ThenBy(d => d.Name))
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameTb = new TextBlock
                {
                    Text = name, Foreground = fg, FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(nameTb, 0);
                grid.Children.Add(nameTb);

                var statusTb = new TextBlock
                {
                    Text = connected ? "✓ Connected" : "Paired",
                    Foreground = dim, FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
                };
                Grid.SetColumn(statusTb, 1);
                grid.Children.Add(statusTb);

                var removeTb = new TextBlock
                {
                    Text = "Remove", Foreground = dim, FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                    Cursor = Cursors.Hand
                };
                removeTb.MouseLeftButtonUp += async (_, e) =>
                {
                    e.Handled = true;
                    await UnpairAsync(info);
                };
                Grid.SetColumn(removeTb, 2);
                grid.Children.Add(removeTb);

                var row = new Border
                {
                    Background = connected
                        ? Application.Current.Resources["PopupAccent"] as SolidColorBrush ?? hoverBg
                        : Brushes.Transparent,
                    CornerRadius = new CornerRadius(6),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(8, 6, 8, 6),
                    Child = grid
                };
                if (!connected)
                {
                    row.MouseEnter += (_, _) => row.Background = hoverBg;
                    row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                }
                row.MouseLeftButtonUp += async (_, _) => await ConnectAsync(info);
                DeviceHost.Children.Add(row);
            }

            FooterHost.Children.Add(MenuChrome.MenuRow("Add new device…", "", () =>
            {
                SafeClose();
                Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            }));
            FooterHost.Children.Add(MenuChrome.MenuRow("Bluetooth settings…", "", () =>
            {
                SafeClose();
                Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            }));
        }

        private async System.Threading.Tasks.Task ConnectAsync(DeviceInformation info)
        {
            if (_busy) return;
            _busy = true;
            Dispatcher.Invoke(() => StatusText.Text = $"Connecting to {info.Name}…");
            try
            {
                // Resolving the device wakes it and brings up the baseband link;
                // touching its services nudges audio profiles to connect.
                var dev = await BluetoothDevice.FromIdAsync(info.Id);
                if (dev != null)
                {
                    try { _ = await dev.GetRfcommServicesAsync(); } catch { }
                    dev.Dispose();
                    await System.Threading.Tasks.Task.Delay(1500);
                }
            }
            catch { }
            await RefreshDevicesAsync();
            _busy = false;
        }

        private async System.Threading.Tasks.Task UnpairAsync(DeviceInformation info)
        {
            try { _ = await info.Pairing.UnpairAsync(); } catch { }
            await RefreshDevicesAsync();
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
