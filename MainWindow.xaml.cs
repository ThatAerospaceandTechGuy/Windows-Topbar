using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using static TopBar.NativeMethods;

namespace TopBar
{
    public partial class MainWindow : Window
    {
        private const int BAR_HEIGHT = 32;
        private const int ANIM_MS = 200;

        private readonly AppBarManager _appBar;
        private readonly MouseEdgeDetector _edgeDetector;
        private readonly DispatcherTimer _clock;
        private readonly DispatcherTimer _statsTimer;
        private readonly ActiveWindowTracker _windowTracker;
        private readonly GlobalHotkey _hotkey;

        private BarConfig _config;
        private bool _isVisible;
        private Window? _activePopup;

        private DispatcherTimer? _battTimer;
        // Live % labels next to the CPU / MEM / SSD symbols
        private readonly Dictionary<string, TextBlock> _statTexts = new();

        // Symbol containers, so hover tooltips can show the live value
        private readonly Dictionary<string, Border> _statTips = new();

        // Contrast-aware foreground (white on dark bar, black on light bar)
        private Color _fgBase = Colors.White;
        private Color FgNormal => Color.FromArgb(0xCC, _fgBase.R, _fgBase.G, _fgBase.B);
        private Color FgDim => Color.FromArgb(0x88, _fgBase.R, _fgBase.G, _fgBase.B);
        private Color FgFull => Color.FromArgb(0xFF, _fgBase.R, _fgBase.G, _fgBase.B);
        private Color HoverBg => Color.FromArgb(0x30, _fgBase.R, _fgBase.G, _fgBase.B);
        // Permanent soft pill behind each icon (macOS-style widget wells)
        private Color SoftBoxBg => Color.FromArgb(0x1C, _fgBase.R, _fgBase.G, _fgBase.B);

        public MainWindow()
        {
            InitializeComponent();

            _config = BarConfig.Load();

            var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            Width = screenW / dpiScale;
            Height = BAR_HEIGHT;
            Left = 0;
            Top = -BAR_HEIGHT;

            int barHeightPx = (int)(BAR_HEIGHT * dpiScale);
            _appBar = new AppBarManager(this) { BarHeight = barHeightPx };

            _edgeDetector = new MouseEdgeDetector
            {
                BarHeight = BAR_HEIGHT,
                EdgeThreshold = 2,
                LeaveMargin = 30,
                RevealDelayMs = _config.RevealDelayMs
            };
            _edgeDetector.MouseEnteredTopEdge += OnReveal;
            _edgeDetector.MouseLeftTopEdge += OnHide;

            // Clock timer (minute precision — 15s is plenty, updated on reveal)
            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _clock.Tick += (_, _) => UpdateClock();
            _clock.Start();

            // One shared tick updates CPU, RAM and SSD together so the bar's
            // inline percentages always refresh in sync (and match the popups).
            // Cheap: CPU and RAM come from cached/raw calls; SSD reads a
            // background-refreshed cache (WMI never runs on the UI thread).
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += (_, _) => UpdateStatsTexts();
            _statsTimer.Start();

            // Prime the fast CPU sampler immediately (starts its own 1s
            // background tick) so clicking the CPU/hardware widget is instant.
            CpuSampler.Sample();

            // Warm the disk-active cache (background PDH) and the static CPU
            // info so the first popup / tick is populated instantly.
            DiskSampler.Sample();
            System.Threading.Tasks.Task.Run(SystemStats.WarmCpuInfo);

            // Active window title + app name (macOS-style)
            _windowTracker = new ActiveWindowTracker();
            _windowTracker.TitleChanged += title =>
                Dispatcher.Invoke(() => WindowTitleText.Text = title);
            _windowTracker.AppChanged += app =>
                Dispatcher.Invoke(() =>
                {
                    AppNameText.Text = string.IsNullOrWhiteSpace(app) ? _config.BarName : app;
                });

            // Global hotkey
            _hotkey = new GlobalHotkey();
            _hotkey.Pressed += () => Dispatcher.Invoke(ToggleBar);

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        // ── Lifecycle ───────────────────────────────────────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Hide from Alt+Tab
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW;
            exStyle &= ~WS_EX_APPWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

            ApplyConfig();
            EnableBlur();
            _edgeDetector.Install();
            UpdateClock();

            try { _hotkey.Register(this, _config.ToggleShortcut); }
            catch { }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _edgeDetector.Dispose();
            _appBar.Dispose();
            _clock.Stop();
            _statsTimer.Stop();
            _battTimer?.Stop();
            _windowTracker.Dispose();
            _hotkey.Dispose();
        }

        // ── Config application ──────────────────────────────────────────────

        private string _lastPanelHash = "";

        private void ComputeForegroundColor()
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(_config.BarColor);
                double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                double eff = (c.A / 255.0) * lum + (1.0 - c.A / 255.0) * 128.0;
                _fgBase = eff > 150 ? Colors.Black : Colors.White;
            }
            catch { _fgBase = Colors.White; }
        }

        private void UpdatePopupResources()
        {
            var app = Application.Current;
            bool isDark = _fgBase == Colors.White;
            app.Resources["PopupFgBright"] = new SolidColorBrush(Color.FromArgb(0xEE, _fgBase.R, _fgBase.G, _fgBase.B));
            app.Resources["PopupFgNormal"] = new SolidColorBrush(Color.FromArgb(0xCC, _fgBase.R, _fgBase.G, _fgBase.B));
            app.Resources["PopupFgDim"] = new SolidColorBrush(Color.FromArgb(0x88, _fgBase.R, _fgBase.G, _fgBase.B));
            app.Resources["PopupFgLabel"] = new SolidColorBrush(Color.FromArgb(0x99, _fgBase.R, _fgBase.G, _fgBase.B));
            app.Resources["PopupAccent"] = new SolidColorBrush(Color.FromArgb(0x40, _fgBase.R, _fgBase.G, _fgBase.B));
            app.Resources["PopupSeparator"] = new SolidColorBrush(Color.FromArgb(0x30, _fgBase.R, _fgBase.G, _fgBase.B));
            app.Resources["PopupThumb"] = new SolidColorBrush(Color.FromArgb(0xDD, _fgBase.R, _fgBase.G, _fgBase.B));
            // Input/button colors adapt to dark vs light background
            app.Resources["PopupInputBg"] = new SolidColorBrush(isDark ? Color.FromRgb(0x2D, 0x2D, 0x2D) : Color.FromRgb(0xF0, 0xF0, 0xF0));
            app.Resources["PopupButtonBg"] = new SolidColorBrush(isDark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xE0, 0xE0, 0xE0));
            app.Resources["PopupInputBorder"] = new SolidColorBrush(isDark ? Color.FromRgb(0x55, 0x55, 0x55) : Color.FromRgb(0xBB, 0xBB, 0xBB));
            app.Resources["PopupInputFg"] = new SolidColorBrush(isDark ? Colors.White : Colors.Black);
            app.Resources["PopupHoverBg"] = new SolidColorBrush(Color.FromArgb(0x20, _fgBase.R, _fgBase.G, _fgBase.B));
        }

        private void ApplyConfig()
        {
            ComputeForegroundColor();
            UpdatePopupResources();

            AppNameText.Text = _config.BarName;
            AppNameText.Foreground = new SolidColorBrush(FgNormal);
            AppleIconPath.Fill = new SolidColorBrush(FgNormal);
            WindowTitleText.Foreground = new SolidColorBrush(FgDim);
            foreach (System.Windows.UIElement child in MenuPanel.Children)
                if (child is Border mb && mb.Child is TextBlock mtb)
                    mtb.Foreground = new SolidColorBrush(FgDim);
            _edgeDetector.RevealDelayMs = _config.RevealDelayMs;

            // Window title visibility
            WindowTitleText.Visibility = _config.ShowWindowTitle ? Visibility.Visible : Visibility.Collapsed;
            if (_config.ShowWindowTitle) _windowTracker.Start(); else _windowTracker.Stop();

            // Startup
            SetStartup(_config.RunAtStartup);

            // Always visible
            if (_config.AlwaysVisible && !_isVisible)
                OnReveal();

            // Rebuild icons if modules, order, or bar color changed
            var panelHash = string.Join(",", _config.RightItemOrder) + "|" +
                $"{_config.ShowBluetooth}," +
                $"{_config.ShowBattery},{_config.ShowBrightness},{_config.ShowWifi},{_config.ShowSound}," +
                $"{_config.ShowClock},{_config.ShowDesktopCorner},{_config.ShowTrayIcons},{_config.ShowSpotlight}," +
                $"{_config.ShowCpu},{_config.ShowMemory},{_config.ShowDisk},{_config.ShowGpu},{_config.ShowControlCenter}|{_config.BarColor}";

            if (panelHash != _lastPanelHash)
            {
                _lastPanelHash = panelHash;
                BuildRightPanel();
                UpdateClock();
            }
        }

        private void BuildRightPanel()
        {
            RightPanel.Children.Clear();
            _statTexts.Clear();
            _statTips.Clear();

            foreach (var itemKey in _config.RightItemOrder)
            {
                UIElement? element = itemKey switch
                {
                    "Bluetooth" when _config.ShowBluetooth => MakeIcon("BtBorder",
                        "M17.71 7.71L12 2h-1v7.59L6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 11 14.41V22h1l5.71-5.71-4.3-4.29 4.3-4.29zM13 5.83l1.88 1.88L13 9.59V5.83zm1.88 10.46L13 18.17v-3.76l1.88 1.88z",
                        "Bluetooth", Bluetooth_Click),
                    "Battery" when _config.ShowBattery => MakeBatteryIndicator(),
                    "Brightness" when _config.ShowBrightness => MakeIcon("BrightBorder",
                        "M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 5-2.24 5-5-2.24-5-5-5zM2 13h2c.55 0 1-.45 1-1s-.45-1-1-1H2c-.55 0-1 .45-1 1s.45 1 1 1zm18 0h2c.55 0 1-.45 1-1s-.45-1-1-1h-2c-.55 0-1 .45-1 1s.45 1 1 1zM11 2v2c0 .55.45 1 1 1s1-.45 1-1V2c0-.55-.45-1-1-1s-1 .45-1 1zm0 18v2c0 .55.45 1 1 1s1-.45 1-1v-2c0-.55-.45-1-1-1s-1 .45-1 1z",
                        "Brightness", Brightness_Click),
                    "Wifi" when _config.ShowWifi => MakeIcon("WifiBorder",
                        "M12 3C7.8 3 3.9 4.6 1 7.4L3.2 9.6C5.5 7.4 8.6 6 12 6s6.5 1.4 8.8 3.6L23 7.4C20.1 4.6 16.2 3 12 3zM12 9c-2.7 0-5.2 1.1-7 2.9L7.2 14c1.3-1.2 3-2 4.8-2s3.5.8 4.8 2l2.2-2.1C17.2 10.1 14.7 9 12 9zm0 6c-1.4 0-2.6.5-3.5 1.5L12 20l3.5-3.5C14.6 15.5 13.4 15 12 15z",
                        "Wi-Fi", Wifi_Click),
                    "Sound" when _config.ShowSound => MakeIcon("SoundBorder",
                        "M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0014 8.3v7.4a4.47 4.47 0 002.5-3.7zM14 3.2v2.1a7 7 0 010 13.4v2.1A9 9 0 0014 3.2z",
                        "Sound", Sound_Click),
                    "TrayIcons" when _config.ShowTrayIcons => MakeIcon("TrayBorder",
                        "M7.41 8.59L12 13.17l4.59-4.58L18 10l-6 6-6-6 1.41-1.41z",
                        "System Tray", Tray_Click),
                    "Cpu" when _config.ShowCpu => MakeStatIcon("CpuTextBlock", StatKind.Cpu, "CPU", Cpu_Click),
                    "Memory" when _config.ShowMemory => MakeStatIcon("MemTextBlock", StatKind.Memory, "Memory", Memory_Click),
                    "Disk" when _config.ShowDisk => MakeStatIcon("DiskTextBlock", StatKind.Disk, "Storage", Disk_Click),
                    "Gpu" when _config.ShowGpu => MakeStatIcon("GpuIcon", StatKind.Gpu, "GPU", Gpu_Click),
                    "ControlCenter" when _config.ShowControlCenter => MakeIcon("CcBorder",
                        "M3 17v2h6v-2H3zM3 5v2h10v-2H3zm10 16v-2h8v-2h-8v-2h-2v6h2zM7 9v2H3v2h4v2h2V9H7zm14 4v-2H11v2h10zm-6-4h2V7h4V5h-4V3h-2v6z",
                        "Control Center", ControlCenter_Click),
                    "Spotlight" when _config.ShowSpotlight => MakeIcon("SpotlightBorder",
                        "M15.5 14h-.79l-.28-.27a6.5 6.5 0 10-.7.7l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0A4.5 4.5 0 1114 9.5 4.5 4.5 0 019.5 14z",
                        "Spotlight Search", Spotlight_Click),
                    "Clock" when _config.ShowClock => MakeClockElement(),
                    "ShowDesktop" when _config.ShowDesktopCorner => MakeShowDesktopCorner(),
                    _ => null
                };

                if (element != null)
                    RightPanel.Children.Add(element);
            }
        }

        // ── Icon factory ────────────────────────────────────────────────────

        private Border MakeIcon(string name, string pathData, string tooltip, MouseButtonEventHandler click)
        {
            var path = new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(FgNormal),
                Data = Geometry.Parse(pathData)
            };
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(path);
            var viewbox = new Viewbox { Width = 15, Height = 15, Child = canvas };

            var border = new Border
            {
                Name = name,
                Background = new SolidColorBrush(SoftBoxBg),
                Cursor = Cursors.Hand,
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = tooltip,
                Child = viewbox,
                VerticalAlignment = VerticalAlignment.Center
            };

            border.MouseLeftButtonDown += click;
            border.MouseEnter += (s, _) => ((Border)s).Background = new SolidColorBrush(HoverBg);
            border.MouseLeave += (s, _) => ((Border)s).Background = new SolidColorBrush(SoftBoxBg);

            return border;
        }

        private enum StatKind { Cpu, Memory, Disk, Gpu }

        /// <summary>macOS-style status widget: symbol + live % in a soft box.</summary>
        private UIElement MakeStatIcon(string name, StatKind kind, string tooltip, MouseButtonEventHandler click)
        {
            var brush = new SolidColorBrush(FgNormal);
            var canvas = new Canvas { Width = 24, Height = 24 };
            switch (kind)
            {
                case StatKind.Cpu: // chip
                    canvas.Children.Add(Rect(7, 7, 10, 10, brush, fill: false));
                    canvas.Children.Add(Rect(10.5, 10.5, 3, 3, brush, fill: true));
                    foreach (double x in new[] { 9.0, 13.0 })
                    {
                        canvas.Children.Add(Rect(x, 3.5, 2, 2.5, brush, fill: true));
                        canvas.Children.Add(Rect(x, 18, 2, 2.5, brush, fill: true));
                    }
                    foreach (double y in new[] { 9.0, 13.0 })
                    {
                        canvas.Children.Add(Rect(3.5, y, 2.5, 2, brush, fill: true));
                        canvas.Children.Add(Rect(18, y, 2.5, 2, brush, fill: true));
                    }
                    break;
                case StatKind.Memory: // RAM stick
                    canvas.Children.Add(Rect(3, 9, 7.5, 7, brush, fill: true));
                    canvas.Children.Add(Rect(13.5, 9, 7.5, 7, brush, fill: true));
                    foreach (double x in new[] { 4.0, 7.5, 11.0, 14.5, 18.0 })
                        canvas.Children.Add(Rect(x, 17, 2, 1.8, brush, fill: true));
                    break;
                case StatKind.Disk: // drive with activity LED
                    canvas.Children.Add(Rect(4, 8, 15, 9, brush, fill: false));
                    canvas.Children.Add(Rect(6.5, 11, 3, 3, brush, fill: true));
                    canvas.Children.Add(Rect(15, 11, 2, 2, brush, fill: true));
                    break;
                case StatKind.Gpu: // display
                    canvas.Children.Add(Rect(4, 5, 16, 10, brush, fill: false));
                    canvas.Children.Add(Rect(11, 16, 2, 3, brush, fill: true));
                    canvas.Children.Add(Rect(8, 20, 8, 1.6, brush, fill: true));
                    break;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Viewbox { Width = 15, Height = 15, Child = canvas, VerticalAlignment = VerticalAlignment.Center });

            // GPU has no cheap live % (WMI-only) — icon alone.
            if (kind != StatKind.Gpu)
            {
                var pct = new TextBlock
                {
                    Text = "—",
                    Foreground = brush,
                    FontSize = 11.5,
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Typography.SetNumeralAlignment(pct, FontNumeralAlignment.Tabular);
                row.Children.Add(pct);
                _statTexts[name] = pct;
            }

            var border = new Border
            {
                Background = new SolidColorBrush(SoftBoxBg),
                Cursor = Cursors.Hand,
                Padding = new Thickness(7, 3, 7, 3),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = tooltip,
                Child = row,
                VerticalAlignment = VerticalAlignment.Center
            };
            border.MouseLeftButtonDown += click;
            border.MouseEnter += (s, _) => ((Border)s).Background = new SolidColorBrush(HoverBg);
            border.MouseLeave += (s, _) => ((Border)s).Background = new SolidColorBrush(SoftBoxBg);
            _statTips[name] = border;
            return border;
        }

        private static Rectangle Rect(double x, double y, double w, double h, SolidColorBrush brush, bool fill)
        {
            var r = new Rectangle { Width = w, Height = h };
            if (fill) r.Fill = brush;
            else { r.Stroke = brush; r.StrokeThickness = 1.4; }
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            return r;
        }

        private UIElement MakeBatteryIndicator()
        {
            var brush = new SolidColorBrush(FgNormal);
            var canvas = new Canvas { Width = 24, Height = 24 };

            // Battery outline group (hidden on AC-only machines)
            var battGroup = new Canvas { Width = 24, Height = 24 };
            battGroup.Children.Add(Rect(3, 7.5, 15, 9, brush, fill: false));
            battGroup.Children.Add(Rect(18.5, 10.25, 2.5, 3.5, brush, fill: true));
            var fill = new Rectangle { Height = 5.5, Fill = brush };
            Canvas.SetLeft(fill, 4.75);
            Canvas.SetTop(fill, 9.25);
            battGroup.Children.Add(fill);
            canvas.Children.Add(battGroup);

            // AC plug (shown when no battery present)
            var flash = new System.Windows.Shapes.Path
            {
                Fill = brush,
                Data = Geometry.Parse("M11 21h-1l1-7H7.5c-.58 0-.57-.32-.38-.66l.07-.12C8.48 10.94 10.42 7.54 13 3h1l-1 7h3.5c.49 0 .56.33.47.51l-.07.15C14.96 14.78 12.34 18.47 11 21z"),
                Visibility = Visibility.Collapsed
            };
            canvas.Children.Add(flash);

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Viewbox { Width = 15, Height = 15, Child = canvas, VerticalAlignment = VerticalAlignment.Center });

            var border = new Border
            {
                Background = new SolidColorBrush(SoftBoxBg),
                Padding = new Thickness(7, 3, 7, 3),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = "Battery",
                Child = row,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            border.MouseLeftButtonDown += Battery_Click;
            border.MouseEnter += (s, _) => ((Border)s).Background = new SolidColorBrush(HoverBg);
            border.MouseLeave += (s, _) => ((Border)s).Background = new SolidColorBrush(SoftBoxBg);

            void refresh()
            {
                int p = GetBatteryPercent();
                if (p >= 0)
                {
                    battGroup.Visibility = Visibility.Visible;
                    flash.Visibility = Visibility.Collapsed;
                    fill.Width = Math.Max(0, 11.5 * p / 100.0);
                    border.ToolTip = $"Battery {p}%";
                }
                else
                {
                    battGroup.Visibility = Visibility.Collapsed;
                    flash.Visibility = Visibility.Visible;
                    border.ToolTip = "Power: AC adapter";
                }
            }
            refresh();

            // Single shared timer (stops the previous one on rebuild)
            _battTimer?.Stop();
            _battTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _battTimer.Tick += (_, _) => refresh();
            _battTimer.Start();

            return border;
        }

        private UIElement MakeClockElement()
        {
            var tb = new TextBlock
            {
                Name = "ClockTextBlock",
                Foreground = new SolidColorBrush(FgNormal),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 2, 0),
                Cursor = Cursors.Hand
            };
            tb.MouseLeftButtonDown += Clock_Click;
            tb.MouseEnter += (_, _) => tb.Foreground = new SolidColorBrush(FgFull);
            tb.MouseLeave += (_, _) => tb.Foreground = new SolidColorBrush(FgNormal);
            return tb;
        }

        private void UpdateStatsTexts()
        {
            if (!_isVisible && !_config.AlwaysVisible) return;

            if (_config.ShowCpu && _statTexts.TryGetValue("CpuTextBlock", out var cpuText))
            {
                double cpu = CpuSampler.Sample().Total;
                cpuText.Text = $"{cpu:F0}%";
                if (_statTips.TryGetValue("CpuTextBlock", out var cpuTip))
                    cpuTip.ToolTip = $"CPU {cpu:F0}% — click for details";
            }
            if (_config.ShowMemory && _statTexts.TryGetValue("MemTextBlock", out var memText))
            {
                uint load = MemorySampler.Sample().LoadPct;
                memText.Text = $"{load}%";
                if (_statTips.TryGetValue("MemTextBlock", out var memTip))
                    memTip.ToolTip = $"Memory {load}% used — click for details";
            }
            if (_config.ShowDisk && _statTexts.TryGetValue("DiskTextBlock", out var diskText))
            {
                double active = DiskSampler.Sample().Active;
                diskText.Text = $"{active:F0}%";
                if (_statTips.TryGetValue("DiskTextBlock", out var diskTip))
                    diskTip.ToolTip = $"SSD active {active:F0}% — click for details";
            }
        }

        private UIElement MakeShowDesktopCorner()
        {
            var inner = new Border
            {
                Background = new SolidColorBrush(HoverBg),
                Width = 2,
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var border = new Border
            {
                Background = Brushes.Transparent,
                Width = 8,
                Margin = new Thickness(4, 0, -12, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Show Desktop",
                Child = inner,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            border.MouseLeftButtonUp += ShowDesktop_Click;
            border.MouseEnter += (s, _) => ((Border)s).Background = new SolidColorBrush(HoverBg);
            border.MouseLeave += (s, _) => ((Border)s).Background = Brushes.Transparent;
            return border;
        }

        // ── Battery helper ──────────────────────────────────────────────────

        private static int GetBatteryPercent()
        {
            try
            {
                var status = System.Windows.Forms.SystemInformation.PowerStatus;
                if (status.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                    return -1;
                return (int)(status.BatteryLifePercent * 100);
            }
            catch
            {
                // Fallback: use WMI
                return GetBatteryWmi();
            }
        }

        private static int GetBatteryWmi()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT EstimatedChargeRemaining FROM Win32_Battery");
                foreach (System.Management.ManagementObject obj in searcher.Get())
                    return Convert.ToInt32(obj["EstimatedChargeRemaining"]);
            }
            catch { }
            return -1;
        }

        // ── Show / hide ─────────────────────────────────────────────────────

        private void ToggleBar()
        {
            if (_isVisible) OnHide(); else OnReveal();
        }

        private void OnReveal()
        {
            if (_isVisible) return;
            _isVisible = true;

            Dispatcher.Invoke(() =>
            {
                if (_config.PushWindows) _appBar.RegisterBar();

                // macOS replica: always-visible bar docks instantly, no slide-in
                if (_config.AlwaysVisible)
                {
                    Top = 0;
                    UpdateClock();
                    UpdateStatsTexts();
                    return;
                }

                var anim = new DoubleAnimation
                {
                    From = -BAR_HEIGHT, To = 0,
                    Duration = TimeSpan.FromMilliseconds(ANIM_MS),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                anim.Completed += (_, _) =>
                {
                    UpdateClock();
                    UpdateStatsTexts();
                };
                BeginAnimation(TopProperty, anim);
            });
        }

        private void OnHide()
        {
            if (!_isVisible || _activePopup != null) return;
            if (_config.AlwaysVisible) return;
            _isVisible = false;

            Dispatcher.Invoke(() =>
            {
                var anim = new DoubleAnimation
                {
                    From = 0, To = -BAR_HEIGHT,
                    Duration = TimeSpan.FromMilliseconds(ANIM_MS),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                anim.Completed += (_, _) =>
                {
                    if (_config.PushWindows) _appBar.UnregisterBar();
                };
                BeginAnimation(TopProperty, anim);
            });
        }

        // ── Blur ────────────────────────────────────────────────────────────

        private void EnableBlur()
        {
            uint color = 0xBF000000;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(_config.BarColor);
                // Convert ARGB to AABBGGRR for SetWindowCompositionAttribute
                color = ((uint)c.A << 24) | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R;
            }
            catch { }

            BlurHelper.CurrentColor = color;
            BlurHelper.EnableBlur(this, color);
        }

        // ── Popup helpers ───────────────────────────────────────────────────

        private void OpenPopup(Window popup, double rightOffset = 12)
        {
            CloseActivePopup();

            popup.Left = Left + Width - popup.Width - rightOffset;
            popup.Top = Top + BAR_HEIGHT + 4;
            popup.Topmost = true;
            _activePopup = popup;

            if (_config.AnimatePopups)
            {
                popup.Opacity = 0;
                popup.Loaded += (_, _) =>
                {
                    var slide = new DoubleAnimation
                    {
                        From = popup.Top - 10, To = popup.Top,
                        Duration = TimeSpan.FromMilliseconds(150),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    var fade = new DoubleAnimation
                    {
                        From = 0, To = 1,
                        Duration = TimeSpan.FromMilliseconds(150)
                    };
                    popup.BeginAnimation(TopProperty, slide);
                    popup.BeginAnimation(OpacityProperty, fade);
                };
            }

            popup.Closed += (_, _) =>
            {
                _activePopup = null;
                HideIfMouseOutside();
            };

            popup.Show();
        }

        private void OpenPopupLeft(Window popup)
        {
            CloseActivePopup();

            popup.Left = Left + 12;
            popup.Top = Top + BAR_HEIGHT + 4;
            popup.Topmost = true;
            _activePopup = popup;

            if (_config.AnimatePopups)
            {
                popup.Opacity = 0;
                popup.Loaded += (_, _) =>
                {
                    var slide = new DoubleAnimation
                    {
                        From = popup.Top - 10, To = popup.Top,
                        Duration = TimeSpan.FromMilliseconds(150),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    var fade = new DoubleAnimation
                    {
                        From = 0, To = 1,
                        Duration = TimeSpan.FromMilliseconds(150)
                    };
                    popup.BeginAnimation(TopProperty, slide);
                    popup.BeginAnimation(OpacityProperty, fade);
                };
            }

            popup.Closed += (_, _) =>
            {
                _activePopup = null;
                HideIfMouseOutside();
            };

            popup.Show();
        }

        private void CloseActivePopup()
        {
            if (_activePopup != null)
            {
                var p = _activePopup;
                _activePopup = null;
                p.Close();
            }
        }

        private void HideIfMouseOutside()
        {
            GetCursorPos(out var pos);
            var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            int barHeightPx = (int)(BAR_HEIGHT * dpiScale);
            if (pos.Y > barHeightPx)
                OnHide();
        }

        // ── Click handlers ──────────────────────────────────────────────────

        private void AppleButton_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is AppleMenuPopup) { CloseActivePopup(); return; }
            OpenPopupLeft(new AppleMenuPopup(OpenTopBarSettings));
        }

        private void AppButton_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is BarMenuPopup) { CloseActivePopup(); return; }
            IntPtr hwnd = TargetAppHwnd();
            string app = hwnd != IntPtr.Zero ? ForegroundMenu.AppNameFor(hwnd) : "";
            if (string.IsNullOrWhiteSpace(app)) app = _config.BarName;
            var h = hwnd;
            OpenPopupLeft(new BarMenuPopup(app, new List<(string, string, Action)>
            {
                ($"About {app}", "", () => { }),
                ("TopBar Settings…", "", () => OpenTopBarSettings()),
                ("Hide", "Win+↓", () => ForegroundActions.HideWindow(h)),
                ("Quit", "Alt+F4", () => ForegroundActions.QuitWindow(h)),
            }));
        }

        /// <summary>
        /// The app window underneath the bar. Clicking the bar activates OUR
        /// window, so handlers must use the tracker's cached handle — never
        /// GetForegroundWindow() at click time. Resolves to zero when unknown.
        /// </summary>
        private IntPtr TargetAppHwnd()
        {
            try
            {
                IntPtr h = _windowTracker.LastHwnd;
                if (h == IntPtr.Zero) h = ForegroundActions.ForegroundHwnd();
                return ForegroundMenu.ValidateTarget(h);
            }
            catch { return IntPtr.Zero; }
        }

        private List<BarMenuPopup.MenuEntry> ToEntries(IntPtr hwnd, List<ForegroundMenu.NativeMenuItem> items)
        {
            var entries = new List<BarMenuPopup.MenuEntry>(items.Count);
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    entries.Add(new BarMenuPopup.MenuEntry { IsSeparator = true });
                    continue;
                }
                var it = item;
                if (it.Submenu != IntPtr.Zero)
                {
                    IntPtr hSub = it.Submenu;
                    entries.Add(new BarMenuPopup.MenuEntry
                    {
                        Label = it.Label,
                        Hint = it.Hint,
                        Enabled = it.Enabled,
                        Submenu = () => ToEntries(hwnd, ForegroundMenu.ReadItems(hSub))
                    });
                }
                else
                {
                    entries.Add(new BarMenuPopup.MenuEntry
                    {
                        Label = it.Label,
                        Hint = it.Hint,
                        Enabled = it.Enabled,
                        Action = it.Enabled ? () => ForegroundMenu.Activate(hwnd, it.Id) : null
                    });
                }
            }
            return entries;
        }

        private void MenuButton_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
            // Same tab → toggle closed; different tab → switch (OpenPopupLeft
            // closes the old popup first).
            if (_activePopup is BarMenuPopup open && open.MenuTitle.Text == tag) { CloseActivePopup(); return; }

            IntPtr hwnd = TargetAppHwnd();

            // Path 1: the app has a real Win32 menu — mirror its exact items.
            if (hwnd != IntPtr.Zero && ForegroundMenu.TryGetSubmenu(hwnd, tag, out IntPtr hSub))
            {
                OpenPopupLeft(new BarMenuPopup(tag, ToEntries(hwnd, ForegroundMenu.ReadItems(hSub))));
                return;
            }

            // Path 2: Explorer — mirror its command bar (File = New folder /
            // New file…, Edit = Cut/Copy/…, View = view dropdown).
            if (hwnd != IntPtr.Zero && (tag == "File" || tag == "Edit" || tag == "View")
                && AppMenuBridge.IsExplorerWindow(hwnd))
            {
                var tailored = AppMenuBridge.GetExplorerItems(hwnd, tag);
                if (tailored.Count > 0)
                {
                    var entries = new List<BarMenuPopup.MenuEntry>(tailored.Count);
                    foreach (var a in tailored)
                        entries.Add(new BarMenuPopup.MenuEntry
                        {
                            Label = a.Label,
                            Hint = a.Hint,
                            Enabled = a.Enabled,
                            Action = a.Enabled ? a.Invoke : null
                        });
                    OpenPopupLeft(new BarMenuPopup(tag, entries));
                    return;
                }
            }

            // Path 3: per-app tailored File/Edit/View for menu-less apps
            // (Chromium, Firefox, Electron, VS Code, Windows Terminal, Notepad)
            // — curated items that fire the app's real shortcuts.
            if (hwnd != IntPtr.Zero && (tag == "File" || tag == "Edit" || tag == "View"))
            {
                string proc = ForegroundMenu.ProcessNameFor(hwnd);
                if (AppMenuProfiles.TryBuild(hwnd, proc, tag, out var prof))
                {
                    OpenPopupLeft(new BarMenuPopup(tag, prof));
                    return;
                }
            }

            // Path 4: open the app's own native control (Office File tab → 
            // Backstage, WPF menus, …) instead of showing a fake popup.
            if (hwnd != IntPtr.Zero && AppMenuBridge.TryExpandNativeControl(hwnd, tag))
            {
                CloseActivePopup();
                return;
            }

            // Path 5: menu-bearing app we couldn't read — open its own native
            // menu with Alt+mnemonic (only if it really has an attached menu).
            if (hwnd != IntPtr.Zero && (tag == "File" || tag == "Edit" || tag == "View")
                && GetMenu(hwnd) != IntPtr.Zero)
            {
                CloseActivePopup();
                ForegroundMenu.SendMnemonic(hwnd, tag);
                return;
            }

            // Last resort — generic shortcuts for the active app (also the
            // source of the Window / Help menus for menu-less apps).
            // Actions re-focus the app first: our popup holds focus at click
            // time, so the keystrokes must not go to ourselves.
            List<(string, string, Action)> items = tag switch
            {
                "File" => new()
                {
                    ("New", "Ctrl+N", Focused(hwnd, ForegroundActions.FileNew)),
                    ("Open…", "Ctrl+O", Focused(hwnd, ForegroundActions.FileOpen)),
                    ("Save", "Ctrl+S", Focused(hwnd, ForegroundActions.FileSave)),
                    ("Print…", "Ctrl+P", Focused(hwnd, ForegroundActions.FilePrint)),
                },
                "Edit" => new()
                {
                    ("Undo", "Ctrl+Z", Focused(hwnd, ForegroundActions.Undo)),
                    ("Redo", "Ctrl+Y", Focused(hwnd, ForegroundActions.Redo)),
                    ("Cut", "Ctrl+X", Focused(hwnd, ForegroundActions.Cut)),
                    ("Copy", "Ctrl+C", Focused(hwnd, ForegroundActions.Copy)),
                    ("Paste", "Ctrl+V", Focused(hwnd, ForegroundActions.Paste)),
                    ("Select All", "Ctrl+A", Focused(hwnd, ForegroundActions.SelectAll)),
                    ("Find…", "Ctrl+F", Focused(hwnd, ForegroundActions.Find)),
                },
                "View" => new()
                {
                    ("Full Screen", "F11", Focused(hwnd, ForegroundActions.FullScreen)),
                    ("Zoom In", "Ctrl+=", Focused(hwnd, ForegroundActions.ZoomIn)),
                    ("Zoom Out", "Ctrl+−", Focused(hwnd, ForegroundActions.ZoomOut)),
                },
                "Window" => new()
                {
                    ("Minimize", "Win+↓", Focused(hwnd, ForegroundActions.Minimize)),
                    ("Close Window", "Alt+F4", Focused(hwnd, ForegroundActions.CloseWindow)),
                    ("Switch Window", "Alt+Tab", Focused(hwnd, ForegroundActions.SwitchWindow)),
                },
                "Help" => new()
                {
                    ("Help", "F1", Focused(hwnd, ForegroundActions.HelpKey)),
                    ("TopBar Settings…", "", () => OpenTopBarSettings()),
                },
                _ => new()
            };
            OpenPopupLeft(new BarMenuPopup(tag, items));
        }

        /// <summary>
        /// Wrap a keystroke action so it re-focuses the target app first.
        /// (Our popup holds focus when a row is clicked.)
        /// </summary>
        private Action Focused(IntPtr hwnd, Action a) => () =>
        {
            try
            {
                if (hwnd != IntPtr.Zero && ForegroundMenu.ValidateTarget(hwnd) != IntPtr.Zero)
                {
                    if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
                a();
            }
            catch { }
        };

        private void MenuButton_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border b)
            {
                b.Background = new SolidColorBrush(HoverBg);
                if (b.Child is TextBlock tb) tb.Foreground = new SolidColorBrush(FgFull);
            }
        }

        private void MenuButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border b)
            {
                b.Background = Brushes.Transparent;
                if (b.Child is TextBlock tb) tb.Foreground = new SolidColorBrush(FgDim);
            }
        }

        private void AppButton_MouseEnter(object sender, MouseEventArgs e)
        {
            AppButton.Background = new SolidColorBrush(HoverBg);
        }

        private void AppButton_MouseLeave(object sender, MouseEventArgs e)
        {
            AppButton.Background = Brushes.Transparent;
        }

        private void OpenTopBarSettings()
        {
            if (_activePopup is SettingsWindow) { CloseActivePopup(); return; }
            CloseActivePopup();

            var settings = new SettingsWindow(_config);
            settings.Left = Left + 12;
            settings.Top = Top + BAR_HEIGHT + 4;
            settings.Topmost = true;
            _activePopup = settings;

            settings.Closed += (_, _) =>
            {
                _activePopup = null;
                HideIfMouseOutside();
            };

            // Live-apply every settings change
            settings.ConfigChanged += () =>
            {
                ApplyConfig();
                EnableBlur();

                try
                {
                    _hotkey.Unregister();
                    _hotkey.Register(this, _config.ToggleShortcut);
                }
                catch { }

                if (!_config.PushWindows && _isVisible)
                    _appBar.UnregisterBar();
                else if (_config.PushWindows && _isVisible)
                    _appBar.RegisterBar();
            };

            if (_config.AnimatePopups)
            {
                settings.Opacity = 0;
                settings.Loaded += (_, _) =>
                {
                    var slide = new DoubleAnimation
                    {
                        From = settings.Top - 10, To = settings.Top,
                        Duration = TimeSpan.FromMilliseconds(150),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) };
                    settings.BeginAnimation(TopProperty, slide);
                    settings.BeginAnimation(OpacityProperty, fade);
                };
            }

            settings.Show();
        }

        private void SettingsButton_MouseEnter(object sender, MouseEventArgs e)
        {
            SettingsButton.Background = new SolidColorBrush(HoverBg);
        }

        private void SettingsButton_MouseLeave(object sender, MouseEventArgs e)
        {
            SettingsButton.Background = Brushes.Transparent;
        }

        private void Wifi_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is WifiPopup) { CloseActivePopup(); return; }
            OpenPopup(new WifiPopup());
        }

        private void Sound_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is SoundPopup) { CloseActivePopup(); return; }
            OpenPopup(new SoundPopup());
        }

        private void Bluetooth_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is BluetoothPopup) { CloseActivePopup(); return; }
            OpenPopup(new BluetoothPopup());
        }

        private void Brightness_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is BrightnessPopup) { CloseActivePopup(); return; }
            OpenPopup(new BrightnessPopup());
        }

        private void Tray_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is TrayPopup) { CloseActivePopup(); return; }
            OpenPopup(new TrayPopup());
        }

        private void Cpu_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is MonitorPopup) { CloseActivePopup(); return; }
            OpenPopup(new MonitorPopup(MonitorKind.Cpu));
        }

        private void Memory_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is MonitorPopup) { CloseActivePopup(); return; }
            OpenPopup(new MonitorPopup(MonitorKind.Memory));
        }

        private void Disk_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is MonitorPopup) { CloseActivePopup(); return; }
            OpenPopup(new MonitorPopup(MonitorKind.Disk));
        }

        private void Battery_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is MonitorPopup) { CloseActivePopup(); return; }
            OpenPopup(new MonitorPopup(MonitorKind.Battery));
        }

        public void Gpu_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is MonitorPopup) { CloseActivePopup(); return; }
            OpenPopup(new MonitorPopup(MonitorKind.Gpu));
        }

        private void ControlCenter_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is ControlCenterPopup) { CloseActivePopup(); return; }
            OpenPopup(new ControlCenterPopup(
                onWifiList: () => OpenPopup(new WifiPopup()),
                onBtList: () => OpenPopup(new BluetoothPopup()),
                onSound: () => OpenPopup(new SoundPopup()),
                onBattery: () => OpenPopup(new MonitorPopup(MonitorKind.Battery))));
        }

        private void Clock_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_activePopup is CalendarPopup) { CloseActivePopup(); return; }

            var cal = new CalendarPopup();

            // Position near the clock element that was clicked
            CloseActivePopup();
            var clickedElement = sender as FrameworkElement;
            if (clickedElement != null)
            {
                var pos = clickedElement.PointToScreen(new Point(clickedElement.ActualWidth / 2, 0));
                var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                // We'll position it after layout so SizeToContent has resolved
                cal.Loaded += (_, _) =>
                {
                    cal.Left = (pos.X / dpiScale) - (cal.ActualWidth / 2);
                    cal.Top = Top + BAR_HEIGHT + 4;
                    // Clamp to screen right edge
                    var screenW = GetSystemMetrics(SM_CXSCREEN) / dpiScale;
                    if (cal.Left + cal.ActualWidth > screenW)
                        cal.Left = screenW - cal.ActualWidth - 8;
                    if (cal.Left < 0) cal.Left = 8;
                };
            }

            cal.Top = Top + BAR_HEIGHT + 4;
            cal.Left = Left + Width - 300; // initial rough position
            cal.Topmost = true;
            _activePopup = cal;

            if (_config.AnimatePopups)
            {
                cal.Opacity = 0;
                cal.Loaded += (_, _) =>
                {
                    var slide = new DoubleAnimation
                    {
                        From = cal.Top - 10, To = cal.Top,
                        Duration = TimeSpan.FromMilliseconds(150),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) };
                    cal.BeginAnimation(TopProperty, slide);
                    cal.BeginAnimation(OpacityProperty, fade);
                };
            }

            cal.Closed += (_, _) =>
            {
                _activePopup = null;
                HideIfMouseOutside();
            };

            cal.Show();
        }

        private void ShowDesktop_Click(object sender, MouseButtonEventArgs e)
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_D, 0, 0, UIntPtr.Zero);
            keybd_event(VK_D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private void Spotlight_Click(object sender, MouseButtonEventArgs e)
        {
            // macOS Spotlight equivalent → Windows Search (Win+S)
            const byte VK_S = 0x53;
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_S, 0, 0, UIntPtr.Zero);
            keybd_event(VK_S, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            e.Handled = true;
        }

        // ── Startup ─────────────────────────────────────────────────────────

        private static void SetStartup(bool enable)
        {
            try
            {
                const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using var key = Registry.CurrentUser.OpenSubKey(keyPath, true);
                if (key == null) return;

                if (enable)
                {
                    var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (exePath != null)
                        key.SetValue("TopBar", $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue("TopBar", false);
                }
            }
            catch { }
        }

        // ── Clock ───────────────────────────────────────────────────────────

        private void UpdateClock()
        {
            // Skip work while hidden in auto-hide mode — refreshed on reveal
            if (!_isVisible && !_config.AlwaysVisible) return;
            // Find the clock TextBlock in the right panel
            foreach (UIElement child in RightPanel.Children)
            {
                if (child is TextBlock tb && tb.Name == "ClockTextBlock")
                {
                    tb.Text = DateTime.Now.ToString("ddd MMM d   h:mm tt");
                    return;
                }
            }
        }
    }
}
