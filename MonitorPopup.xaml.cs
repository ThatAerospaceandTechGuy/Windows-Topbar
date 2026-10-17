using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopBar
{
    public enum MonitorKind { Cpu, Gpu, Memory, Disk, Battery }

    /// <summary>
    /// Hardware dropdown. The element tree is built once; a 1s tick updates
    /// values in place (no rebuilds, no flicker, no scroll reset). CPU uses
    /// the fast NtQuerySystemInformation sampler; WMI runs on background
    /// threads at a slower cadence and only while the popup is open.
    /// </summary>
    public partial class MonitorPopup : Window
    {
        private bool _ready, _closing, _busy;
        private DispatcherTimer? _leaveTimer;
        private DispatcherTimer? _tickTimer;
        private int _tick;
        private readonly MonitorKind _kind;

        private const int GraphSamples = 60;

        // Live-updated elements (created once in BuildSkeleton)
        private TextBlock? _heroValue;
        private Sparkline? _heroGraph;
        private readonly List<(TextBlock Name, TextBlock Val)> _procRows = new();

        private TextBlock? _cpuProcessor, _cpuCores, _cpuUptime, _cpuThreads;
        private TextBlock? _gpuName, _gpuVram, _gpuDriver;
        private TextBlock? _memUsed, _memFree, _memPressure;
        private TextBlock? _diskDrive, _diskRead, _diskWrite;
        private TextBlock? _batSource, _batTime, _batHealth;

        public MonitorPopup(MonitorKind kind)
        {
            InitializeComponent();
            _kind = kind;
            Loaded += (_, _) => MenuChrome.Apply(this);
            ContentRendered += (_, _) => _ready = true;

            MonitorTitle.Text = kind switch
            {
                MonitorKind.Cpu => "CPU",
                MonitorKind.Gpu => "GPU",
                MonitorKind.Memory => "Memory",
                MonitorKind.Disk => "Storage",
                MonitorKind.Battery => "Battery",
                _ => "System"
            };

            BuildSkeleton();

            _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tickTimer.Tick += async (_, _) => await OnTickAsync();
            _tickTimer.Start();
            _ = OnTickAsync();
        }

        // ── Theme brushes ────────────────────────────────────────────────

        private SolidColorBrush FgBright => Application.Current.Resources["PopupFgBright"] as SolidColorBrush
            ?? new SolidColorBrush(Colors.White);
        private SolidColorBrush FgDim => Application.Current.Resources["PopupFgLabel"] as SolidColorBrush
            ?? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

        private Sparkline MakeGraph(double height, bool grid)
        {
            var c = FgBright.Color;
            var line = new SolidColorBrush(Color.FromArgb(0xE6, c.R, c.G, c.B));
            line.Freeze();
            var fill = new LinearGradientBrush(
                Color.FromArgb(0x38, c.R, c.G, c.B),
                Color.FromArgb(0x06, c.R, c.G, c.B),
                new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            var gridBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            gridBrush.Freeze();
            var g = new Sparkline(GraphSamples, line, fill, grid ? gridBrush : null)
            {
                Height = height,
                Margin = new Thickness(0, 2, 0, 8)
            };
            DetailHost.Children.Add(g);
            return g;
        }

        // ── Skeleton construction ────────────────────────────────────────

        private TextBlock AddHero()
        {
            var v = new TextBlock
            {
                Text = "—", Foreground = FgBright, FontSize = 30,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 0),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = 32
            };
            Typography.SetNumeralAlignment(v, FontNumeralAlignment.Tabular);
            DetailHost.Children.Add(v);
            return v;
        }

        private TextBlock AddRow(string label, string initial = "—", double topMargin = 2)
        {
            var grid = new Grid { Margin = new Thickness(0, topMargin, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = FgDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var v = new TextBlock
            {
                Text = initial, Foreground = FgBright, FontSize = 12,
                TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap,
                MaxWidth = 190, VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(v, FontNumeralAlignment.Tabular);
            Grid.SetColumn(l, 0); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
            DetailHost.Children.Add(grid);
            return v;
        }

        private (TextBlock Name, TextBlock Val) AddProcRow()
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock
            {
                Text = "—", Foreground = FgDim, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var val = new TextBlock
            {
                Text = "", Foreground = FgBright, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(val, FontNumeralAlignment.Tabular);
            Grid.SetColumn(name, 0); Grid.SetColumn(val, 1);
            grid.Children.Add(name); grid.Children.Add(val);
            DetailHost.Children.Add(grid);
            return (name, val);
        }

        private void AddHeader(string text)
        {
            DetailHost.Children.Add(new TextBlock
            {
                Text = text, Foreground = FgDim, FontSize = 11,
                Margin = new Thickness(0, 12, 0, 4)
            });
        }

        private void FillProcRows(List<(TextBlock Name, TextBlock Val)> rows, List<(string Name, string Detail)> data)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (i < data.Count)
                {
                    rows[i].Name.Text = data[i].Name;
                    rows[i].Val.Text = data[i].Detail;
                    rows[i].Name.Visibility = rows[i].Val.Visibility = Visibility.Visible;
                }
                else
                {
                    rows[i].Name.Visibility = rows[i].Val.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void BuildSkeleton()
        {
            switch (_kind)
            {
                case MonitorKind.Cpu:
                {
                    _heroValue = AddHero();
                    _heroGraph = MakeGraph(46, grid: true);
                    _cpuProcessor = AddRow("Processor");
                    _cpuCores = AddRow("Cores");
                    _cpuUptime = AddRow("Uptime");
                    _cpuThreads = AddRow("Threads (system)");
                    break;
                }
                case MonitorKind.Gpu:
                {
                    _heroValue = AddHero();
                    _heroGraph = MakeGraph(46, grid: true);
                    _gpuName = AddRow("GPU");
                    _gpuVram = AddRow("VRAM");
                    _gpuDriver = AddRow("Driver");
                    break;
                }
                case MonitorKind.Memory:
                {
                    _heroValue = AddHero();
                    _heroGraph = MakeGraph(46, grid: true);
                    _memUsed = AddRow("Used");
                    _memFree = AddRow("Free");
                    _memPressure = AddRow("Memory pressure");
                    AddHeader("Top consumers");
                    for (int i = 0; i < 6; i++) _procRows.Add(AddProcRow());
                    break;
                }
                case MonitorKind.Disk:
                {
                    _heroValue = AddHero();
                    _heroGraph = MakeGraph(46, grid: true);
                    _diskDrive = AddRow("Drive");
                    _diskRead = AddRow("Read");
                    _diskWrite = AddRow("Write");
                    break;
                }
                case MonitorKind.Battery:
                {
                    _heroValue = AddHero();
                    _batSource = AddRow("Power source");
                    _batTime = AddRow("Time remaining");
                    _batHealth = AddRow("Health");
                    break;
                }
            }
        }

        // ── Tick ─────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task OnTickAsync()
        {
            if (_busy || _closing) return;
            _busy = true;
            try
            {
                switch (_kind)
                {
                    case MonitorKind.Cpu: await RefreshCpuAsync(); break;
                    case MonitorKind.Gpu: await RefreshGpuAsync(); break;
                    case MonitorKind.Memory: await RefreshMemoryAsync(); break;
                    case MonitorKind.Disk: await RefreshDiskAsync(); break;
                    case MonitorKind.Battery: await RefreshBatteryAsync(); break;
                }
            }
            catch { }
            finally
            {
                _busy = false;
                _tick++;
            }
        }

        private async System.Threading.Tasks.Task RefreshCpuAsync()
        {
            double total = CpuSampler.Sample().Total;
            _heroValue!.Text = $"{total:F1}%";
            _heroGraph!.Add(total);
            _cpuUptime!.Text = SystemStats.GetUptime();

            if (_tick == 0)
            {
                var infoTask = System.Threading.Tasks.Task.Run(SystemStats.GetCpuInfo);
                var threadsTask = System.Threading.Tasks.Task.Run(SystemStats.GetProcessThreadCount);
                await System.Threading.Tasks.Task.WhenAll(infoTask, threadsTask);
                if (_closing) return;
                var info = infoTask.Result;
                _cpuProcessor!.Text = info.TryGetValue("Processor", out var p) ? p : "—";
                _cpuCores!.Text = info.TryGetValue("Cores", out var cr) ? cr : "—";
                _cpuThreads!.Text = threadsTask.Result.ToString("N0");
            }
            else if (_tick % 3 == 0)
            {
                int threads = await System.Threading.Tasks.Task.Run(SystemStats.GetProcessThreadCount);
                if (_closing) return;
                _cpuThreads!.Text = threads.ToString("N0");
            }
        }

        private async System.Threading.Tasks.Task RefreshGpuAsync()
        {
            if (_tick == 0)
            {
                var info = await System.Threading.Tasks.Task.Run(SystemStats.GetGpuInfo);
                if (_closing) return;
                _gpuName!.Text = info.TryGetValue("GPU", out var n) ? n : "No GPU info";
                _gpuVram!.Text = info.TryGetValue("VRAM", out var v) ? v : "—";
                _gpuDriver!.Text = info.TryGetValue("Driver", out var d) ? d : "—";
            }

            if (_tick == 0 || _tick % 2 == 1)
            {
                var util = await System.Threading.Tasks.Task.Run(SystemStats.GetGpuUsage);
                if (_closing) return;
                if (util.HasValue)
                {
                    _heroValue!.Text = $"{util.Value:F0}%";
                    _heroGraph!.Add(util.Value);
                }
                else
                {
                    _heroValue!.Text = "—";
                }
            }
        }

        private async System.Threading.Tasks.Task RefreshMemoryAsync()
        {
            var (total, avail, load) = MemorySampler.Sample();
            ulong used = total > avail ? total - avail : 0;
            _heroValue!.Text = $"{load}%";
            _heroGraph!.Add(load);
            _memUsed!.Text = $"{SystemStats.FormatBytes(used)} / {SystemStats.FormatBytes(total)}";
            _memFree!.Text = SystemStats.FormatBytes(avail);
            _memPressure!.Text = load < 60 ? "Green — comfortable" : load < 85 ? "Yellow — under pressure" : "Red — strained";

            if (_tick % 3 == 2)
            {
                var procs = await System.Threading.Tasks.Task.Run(() => SystemStats.TopMemoryProcesses(_procRows.Count));
                if (_closing) return;
                FillProcRows(_procRows, procs);
            }
        }

        private async System.Threading.Tasks.Task RefreshDiskAsync()
        {
            // DiskSampler reads a cheap cached PDH value, so refresh every tick
            // to stay perfectly in sync with the bar's SSD percentage.
            var result = await System.Threading.Tasks.Task.Run(() =>
            {
                var (active, r, w) = DiskSampler.Sample();
                var disk = SystemStats.GetSystemDisk();
                return (active, r, w, disk);
            });
            if (_closing) return;

            _heroValue!.Text = $"{result.active:F0}%";
            _heroGraph!.Add(result.active);
            _diskDrive!.Text = $"{result.disk.Name} · {result.disk.Free} free of {result.disk.Total}";
            _diskRead!.Text = $"{result.r:F1} MB/s";
            _diskWrite!.Text = $"{result.w:F1} MB/s";
        }

        private async System.Threading.Tasks.Task RefreshBatteryAsync()
        {
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                _heroValue!.Text = ps.BatteryLifePercent >= 0 ? $"{(int)(ps.BatteryLifePercent * 100)}%" : "—";
                _batSource!.Text = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "AC adapter" : "Battery";
                _batTime!.Text = ps.BatteryLifeRemaining > 0
                    ? $"{ps.BatteryLifeRemaining / 3600}h {(ps.BatteryLifeRemaining % 3600) / 60}m"
                    : "—";
            }
            catch { }

            if (_tick == 0)
            {
                var info = await System.Threading.Tasks.Task.Run(SystemStats.GetBatteryInfo);
                if (_closing) return;
                _batHealth!.Text = info.TryGetValue("Health", out var h) ? h : "—";
            }
        }

        // ── Close logic ─────────────────────────────────────────────────

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
            _tickTimer?.Stop();
            Close();
        }
    }
}
