using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.System.Power;

namespace TopBar
{
    /// <summary>
    /// Windows 11 Quick Settings copy: pill tiles with live subtitles and the
    /// system accent for active states, sliders, battery + settings footer.
    /// Tile chevrons jump to the full TopBar list popups (same backend).
    /// Everything here drives the real system: radios, DND, energy saver,
    /// brightness (WMI) and the default render endpoint (NAudio).
    /// </summary>
    public partial class ControlCenterPopup : Window
    {
        private bool _ready, _closing, _refreshing;
        private bool _suppressBright, _suppressVol;
        private DispatcherTimer? _leaveTimer, _refreshTimer, _brightDebounce;
        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _device;
        private int _pendingBright;

        private readonly Action _onWifiList;
        private readonly Action _onBtList;
        private readonly Action _onSound;
        private readonly Action _onBattery;

        private Radio? _wifiRadio, _btRadio;
        private List<Radio> _allRadios = new();
        private SolidColorBrush _accentBg = new(Color.FromRgb(0x00, 0x78, 0xD4));
        private SolidColorBrush _accentFg = new(Colors.White);
        private SolidColorBrush _accentDim = new(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

        [DllImport("dwmapi.dll")] private static extern int DwmGetColorizationColor(out uint color, out bool opaque);

        public ControlCenterPopup(Action onWifiList, Action onBtList, Action onSound, Action onBattery)
        {
            InitializeComponent();
            _onWifiList = onWifiList;
            _onBtList = onBtList;
            _onSound = onSound;
            _onBattery = onBattery;

            Loaded += (_, _) => MenuChrome.Apply(this);
            ContentRendered += (_, _) => { _ready = true; StartRefresh(); };
            Closed += (_, _) => { _refreshTimer?.Stop(); _leaveTimer?.Stop(); _brightDebounce?.Stop(); };

            ComputeAccent();

            _suppressBright = true;
            int current = GetBrightness();
            if (current >= 0) BrightnessSlider.Value = current;
            else BrightnessSlider.IsEnabled = false;
            _suppressBright = false;

            InitAudio();
            InitAsync();
        }

        private void ComputeAccent()
        {
            try
            {
                if (DwmGetColorizationColor(out uint c, out _) == 0)
                {
                    var col = Color.FromArgb(0xFF, (byte)(c >> 16), (byte)(c >> 8), (byte)c);
                    _accentBg = new SolidColorBrush(col);
                    double lum = 0.299 * col.R + 0.587 * col.G + 0.114 * col.B;
                    bool dark = lum <= 150;
                    _accentFg = new SolidColorBrush(dark ? Colors.White : Colors.Black);
                    _accentDim = new SolidColorBrush(dark
                        ? Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)
                        : Color.FromArgb(0xB0, 0x00, 0x00, 0x00));
                    return;
                }
            }
            catch { }
        }

        // ── State ───────────────────────────────────────────────────────

        private async void InitAsync()
        {
            try
            {
                var radios = await Radio.GetRadiosAsync();
                _allRadios = radios.ToList();
                _wifiRadio = _allRadios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                _btRadio = _allRadios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            }
            catch { }
            await RefreshTilesAsync();
        }

        private void StartRefresh()
        {
            if (_refreshTimer == null)
            {
                _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _refreshTimer.Tick += async (_, _) => await RefreshTilesAsync();
            }
            _refreshTimer.Start();
        }

        private async System.Threading.Tasks.Task RefreshTilesAsync()
        {
            if (_closing || _refreshing) return;
            _refreshing = true;
            try
            {
                bool wifiOn = _wifiRadio == null || _wifiRadio.State == RadioState.On;
                bool btOn = _btRadio == null || _btRadio.State == RadioState.On;
                bool airplane = _allRadios.Count > 0 && _allRadios.All(r => r.State == RadioState.Off);

                string wifiSub = "N/A";
                if (_wifiRadio != null)
                {
                    if (!wifiOn) wifiSub = "Off";
                    else
                    {
                        var st = await WifiNative.GetStatusAsync();
                        wifiSub = string.IsNullOrEmpty(st.CurrentSsid) ? "Not connected" : st.CurrentSsid;
                    }
                }

                string btSub = "N/A";
                if (_btRadio != null)
                {
                    if (!btOn) btSub = "Off";
                    else btSub = await ConnectedBtNameAsync() ?? "Not connected";
                }

                bool eco = GetEnergySaver();
                bool dnd = GetDnd();

                if (_closing) return;
                Dispatcher.Invoke(() =>
                {
                    if (_closing) return;
                    PaintTile(TileWifi, WifiCanvas, wifiSub, wifiOn, WifiChevron);
                    PaintTile(TileBt, BtCanvas, btSub, btOn, BtChevron);
                    PaintTile(TileAir, AirCanvas, airplane ? "On" : "Off", airplane, null);
                    PaintTile(TileDnd, DndCanvas, dnd ? "On" : "Off", dnd, null);
                    PaintTile(TileEco, EcoCanvas, eco ? "On" : "Off", eco, null);
                    RefreshBatteryFooter();
                    SyncSliders();
                });
            }
            catch { }
            finally { _refreshing = false; }
        }

        private static async System.Threading.Tasks.Task<string?> ConnectedBtNameAsync()
        {
            try
            {
                var found = await DeviceInformation.FindAllAsync(
                    BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                    new[] { "System.Devices.Aep.IsConnected" });
                foreach (var d in found)
                {
                    try
                    {
                        if (d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out object? v)
                            && v is bool b && b)
                            return string.IsNullOrWhiteSpace(d.Name) ? "Connected" : d.Name;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private void PaintTile(Border tile, Canvas canvas, string sub, bool active, TextBlock? chevron)
        {
            var normalBg = Application.Current.Resources["PopupButtonBg"] as SolidColorBrush
                ?? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
            var normalFg = Application.Current.Resources["PopupFgNormal"] as SolidColorBrush
                ?? new SolidColorBrush(Colors.White);
            var labelFg = Application.Current.Resources["PopupFgBright"] as SolidColorBrush
                ?? new SolidColorBrush(Colors.White);
            var dimFg = Application.Current.Resources["PopupFgLabel"] as SolidColorBrush
                ?? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

            tile.Background = active ? _accentBg : normalBg;
            TintCanvas(canvas, active ? _accentFg : normalFg);
            // Title + subtitle are rows 1/2 of the tile grid
            if (tile.Child is Grid g && g.Children.Count >= 3)
            {
                if (g.Children[1] is TextBlock title)
                    title.Foreground = active ? _accentFg : labelFg;
                if (g.Children[2] is TextBlock st)
                {
                    st.Text = sub;
                    st.Foreground = active ? _accentDim : dimFg;
                }
            }
            if (chevron != null)
                chevron.Foreground = active ? _accentDim : dimFg;
        }

        private static void TintCanvas(Canvas canvas, SolidColorBrush brush)
        {
            foreach (var c in canvas.Children)
            {
                if (c is Shape s)
                {
                    if (s.Fill != null) s.Fill = brush;
                    if (s.Stroke != null) s.Stroke = brush;
                }
            }
        }

        private void RefreshBatteryFooter()
        {
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                if (ps.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                    BattText.Text = "Plugged in";
                else
                {
                    int pct = (int)(ps.BatteryLifePercent * 100);
                    bool charging = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.Charging) != 0;
                    BattText.Text = pct >= 0 ? $"{pct}%{(charging ? " ⚡" : "")}" : "—";
                }
            }
            catch { BattText.Text = "—"; }
        }

        // ── Tile clicks ─────────────────────────────────────────────────

        private async void WifiTile_Click(object sender, MouseButtonEventArgs e)
        {
            if (_wifiRadio == null) return;
            try { await _wifiRadio.SetStateAsync(_wifiRadio.State == RadioState.On ? RadioState.Off : RadioState.On); } catch { }
            await RefreshTilesAsync();
        }

        private void WifiChevron_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SafeClose();
            try { _onWifiList(); } catch { }
        }

        private async void BtTile_Click(object sender, MouseButtonEventArgs e)
        {
            if (_btRadio == null) return;
            try { await _btRadio.SetStateAsync(_btRadio.State == RadioState.On ? RadioState.Off : RadioState.On); } catch { }
            await RefreshTilesAsync();
        }

        private void BtChevron_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SafeClose();
            try { _onBtList(); } catch { }
        }

        private async void AirTile_Click(object sender, MouseButtonEventArgs e)
        {
            if (_allRadios.Count == 0) return;
            bool anyOn = _allRadios.Any(r => r.State == RadioState.On);
            foreach (var r in _allRadios)
            {
                try { await r.SetStateAsync(anyOn ? RadioState.Off : RadioState.On); } catch { }
            }
            await RefreshTilesAsync();
        }

        private void DndTile_Click(object sender, MouseButtonEventArgs e)
        {
            SetDnd(!GetDnd());
            _ = RefreshTilesAsync();
        }

        private void EcoTile_Click(object sender, MouseButtonEventArgs e)
        {
            if (TrySetEnergySaver(!GetEnergySaver()))
            {
                _ = RefreshTilesAsync();
                return;
            }
            // Toggling energy saver needs elevation; fall back to Settings.
            SafeClose();
            ForegroundActions.OpenSettings("batterysaver");
        }

        private void A11yTile_Click(object sender, MouseButtonEventArgs e)
        {
            SafeClose();
            ForegroundActions.OpenSettings("easeofaccess");
        }

        private void SoundChevron_Click(object sender, MouseButtonEventArgs e)
        {
            SafeClose();
            try { _onSound(); } catch { }
        }

        private void Battery_Click(object sender, MouseButtonEventArgs e)
        {
            SafeClose();
            try { _onBattery(); } catch { }
        }

        private void Gear_Click(object sender, MouseButtonEventArgs e)
        {
            SafeClose();
            ForegroundActions.OpenSettings("");
        }

        // ── Brightness (WMI, laptop panels) ─────────────────────────────

        private static int GetBrightness()
        {
            try
            {
                using var s = new System.Management.ManagementObjectSearcher("root\\WMI",
                    "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (System.Management.ManagementObject o in s.Get())
                    return Convert.ToInt32(o["CurrentBrightness"]);
            }
            catch { }
            return -1;
        }

        private static bool SetBrightness(int value)
        {
            try
            {
                using var s = new System.Management.ManagementObjectSearcher("root\\WMI",
                    "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    o.InvokeMethod("WmiSetBrightness", new object[] { 1, value });
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressBright) return;
            BrightnessSlider.ToolTip = $"{(int)e.NewValue}%";
            _pendingBright = (int)e.NewValue;
            if (_brightDebounce == null)
            {
                _brightDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                _brightDebounce.Tick += (_, _) => { _brightDebounce.Stop(); SetBrightness(_pendingBright); };
            }
            _brightDebounce.Stop();
            _brightDebounce.Start();
        }

        // ── Volume (NAudio, lazy) ───────────────────────────────────────

        private void InitAudio()
        {
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _suppressVol = true;
                VolumeSlider.Value = Math.Round((_device.AudioEndpointVolume?.MasterVolumeLevelScalar ?? 0) * 100);
                VolumeSlider.ToolTip = _device.AudioEndpointVolume?.Mute == true ? "Muted" : $"{VolumeSlider.Value:F0}%";
                _suppressVol = false;
                UpdateMuteIcon();
            }
            catch
            {
                VolumeSlider.IsEnabled = false;
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressVol || _device?.AudioEndpointVolume == null) return;
            try
            {
                var vol = _device.AudioEndpointVolume;
                if (vol.Mute && e.NewValue > 0) vol.Mute = false;
                vol.MasterVolumeLevelScalar = (float)(e.NewValue / 100.0);
                VolumeSlider.ToolTip = $"{(int)e.NewValue}%";
                UpdateMuteIcon();
            }
            catch { }
        }

        private const string ICON_UNMUTED =
            "M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0014 8.3v7.4a4.47 4.47 0 002.5-3.7zM14 3.2v2.1a7 7 0 010 13.4v2.1A9 9 0 0014 3.2z";
        private const string ICON_MUTED =
            "M3 9v6h4l5 5V4L7 9H3zm13 .8v4.4L19.2 12 16 8.8zm3.5-3.3L21 8l-3 3 3 3-1.5 1.5L16.5 12l-3 3L12 13.5l3-3-3-3L13.5 6l3 3 3-3z";

        private void SpkButton_Click(object sender, MouseButtonEventArgs e)
        {
            if (_device?.AudioEndpointVolume == null) return;
            try
            {
                var vol = _device.AudioEndpointVolume;
                vol.Mute = !vol.Mute;
                UpdateMuteIcon();
                VolumeSlider.ToolTip = vol.Mute ? "Muted" : $"{(int)VolumeSlider.Value}%";
            }
            catch { }
        }

        private void UpdateMuteIcon()
        {
            if (_device?.AudioEndpointVolume == null) return;
            bool muted = _device.AudioEndpointVolume.Mute || _device.AudioEndpointVolume.MasterVolumeLevelScalar <= 0.0001f;
            SpkIcon.Data = Geometry.Parse(muted ? ICON_MUTED : ICON_UNMUTED);
        }

        /// <summary>Keeps sliders in step with changes made elsewhere.</summary>
        private void SyncSliders()
        {
            try
            {
                if (!_suppressVol && _device?.AudioEndpointVolume != null && !VolumeSlider.IsMouseCaptureWithin)
                {
                    double v = Math.Round(_device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                    if (Math.Abs(VolumeSlider.Value - v) > 0.5)
                    {
                        _suppressVol = true;
                        VolumeSlider.Value = v;
                        _suppressVol = false;
                    }
                }
                UpdateMuteIcon();

                if (!_suppressBright && (_brightDebounce == null || !_brightDebounce.IsEnabled)
                    && !BrightnessSlider.IsMouseCaptureWithin)
                {
                    int b = GetBrightness();
                    if (b >= 0 && Math.Abs(BrightnessSlider.Value - b) > 0.5)
                    {
                        _suppressBright = true;
                        BrightnessSlider.Value = b;
                        _suppressBright = false;
                    }
                }
            }
            catch { }
        }

        // ── Do Not Disturb (documented registry value) ──────────────────

        private const string DndKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings";
        private const string DndValue = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

        private static bool GetDnd()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DndKey, false);
                if (key?.GetValue(DndValue) is int i) return i == 0;
            }
            catch { }
            return false;
        }

        private static void SetDnd(bool on)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(DndKey);
                key?.SetValue(DndValue, on ? 0 : 1, RegistryValueKind.DWord);
            }
            catch { }
        }

        // ── Energy saver (needs elevation to change) ────────────────────

        private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
        private const string EnergyValue = "EnergySaverState";

        private static bool GetEnergySaver()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PowerKey, false);
                if (key?.GetValue(EnergyValue) is int i) return i == 1;
            }
            catch { }
            try { return PowerManager.EnergySaverStatus == EnergySaverStatus.On; } catch { }
            return false;
        }

        private static bool TrySetEnergySaver(bool on)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PowerKey, true);
                if (key == null) return false;
                key.SetValue(EnergyValue, on ? 1 : 2, RegistryValueKind.DWord);
                return true;
            }
            catch { return false; }
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
        private void SafeClose()
        {
            if (_closing) return;
            _closing = true;
            _leaveTimer?.Stop();
            _refreshTimer?.Stop();
            _brightDebounce?.Stop();
            try { _device?.Dispose(); _enumerator?.Dispose(); } catch { }
            Close();
        }
    }
}
