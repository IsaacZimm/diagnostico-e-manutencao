using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace WPM.App.Monitoring;

[Flags]
internal enum HudMetrics
{
    None = 0, Processor = 1, Graphics = 2, Memory = 4, Temperatures = 8, Fps = 16, FrameTimes = 32, Clocks = 64, Power = 128
}

internal enum HudPosition { TopLeft, TopRight, BottomLeft, BottomRight }

internal sealed record HudOptions(HudPosition Position, Color Highlight, HudMetrics Metrics)
{
    public bool NeedsFrames => (Metrics & (HudMetrics.Fps | HudMetrics.FrameTimes)) != 0;
}

public partial class HudWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const int IgnoreMouse = 0x20;
    private const int ToolWindow = 0x80;
    private const int NoActivation = 0x08000000;
    private const uint MonitorDefaultToNearest = 2;
    private const uint PositionWithoutResizeOrActivation = 0x10 | 0x01 | 0x0200;
    private static readonly nint TopmostLayer = new(-1);
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("pt-BR");
    private nint handle;
    private nint display;
    private HudPosition position;
    private Color? highlight;

    public HudWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            _ = SetWindowLong(handle, ExtendedStyleIndex,
                GetWindowLong(handle, ExtendedStyleIndex) | IgnoreMouse | ToolWindow | NoActivation);
        };
        SizeChanged += (_, _) => ArrangeOnScreen();
        ContentRendered += (_, _) => ArrangeOnScreen();
    }

    internal void Render(HudOptions options, SensorSample? sensors, FrameMeasurement? frames)
    {
        position = options.Position;
        if (options.Highlight != highlight)
        {
            highlight = options.Highlight;
            var accent = new SolidColorBrush(options.Highlight);
            accent.Freeze();
            Heading.Foreground = accent;
            Frame.BorderBrush = accent;
        }

        Heading.Text = options.NeedsFrames && frames is not null ? frames.Process : "Desempenho ao vivo";
        var rows = new List<PerformanceRow>();
        if (options.Metrics.HasFlag(HudMetrics.Fps))
        {
            rows.Add(new("FPS de apresentação", frames is null ? "—" : string.Create(DisplayCulture, $"{frames.Fps:0.#}")));
        }
        if (options.Metrics.HasFlag(HudMetrics.FrameTimes))
        {
            rows.Add(new("Tempo médio de quadro", frames is null ? "—" : string.Create(DisplayCulture, $"{frames.AverageMs:0.#} ms")));
            rows.Add(new("Quadro lento · P99", frames is null ? "—" : string.Create(DisplayCulture, $"{frames.SlowFrameMs:0.#} ms")));
        }
        if (options.Metrics.HasFlag(HudMetrics.Processor))
        {
            rows.Add(new("CPU", Metric(SensorType.Load, sensors?.CpuLoad)));
        }
        if (options.Metrics.HasFlag(HudMetrics.Graphics))
        {
            rows.Add(new("GPU", Metric(SensorType.Load, sensors?.GpuLoad)));
        }
        if (options.Metrics.HasFlag(HudMetrics.Memory))
        {
            rows.Add(new("RAM", Metric(SensorType.Load, sensors?.RamLoad)));
            rows.Add(new("Memória da GPU", Metric(SensorType.Load, sensors?.VramLoad)));
        }
        if (options.Metrics.HasFlag(HudMetrics.Temperatures))
        {
            rows.Add(new("Temperatura da CPU", Metric(SensorType.Temperature, sensors?.CpuTemperature)));
            rows.Add(new("Temperatura da GPU", Metric(SensorType.Temperature, sensors?.GpuTemperature)));
        }
        if (options.Metrics.HasFlag(HudMetrics.Clocks))
        {
            rows.Add(new("Maior clock da CPU", Metric(SensorType.Clock, sensors?.CpuClock)));
            rows.Add(new("Clock da GPU", Metric(SensorType.Clock, sensors?.GpuClock)));
        }
        if (options.Metrics.HasFlag(HudMetrics.Power))
        {
            rows.Add(new("Potência do pacote CPU", Metric(SensorType.Power, sensors?.CpuPower)));
            rows.Add(new("Potência da GPU", Metric(SensorType.Power, sensors?.GpuPower)));
        }

        Metrics.ItemsSource = rows;
        ArrangeOnScreen();
    }

    private static string Metric(SensorType type, double? value) => value.HasValue ? SensorDisplay.Value(type, value) : "—";

    private void ArrangeOnScreen()
    {
        if (handle == 0 || !IsVisible)
        {
            return;
        }

        nint foreground = ForegroundProcess.GetForegroundWindow();
        if (foreground != 0 && ForegroundProcess.GetWindowThreadProcessId(foreground, out int pid) != 0 &&
            pid != Environment.ProcessId)
        {
            display = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        }

        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (display == 0 || !GetMonitorInfo(display, ref monitor))
        {
            display = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (display == 0 || !GetMonitorInfo(display, ref monitor))
            {
                return;
            }
        }
        if (!GetWindowRect(handle, out var overlay))
        {
            return;
        }

        int margin = (int)Math.Round(14 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        int width = overlay.Right - overlay.Left;
        int height = overlay.Bottom - overlay.Top;
        var bounds = monitor.DisplayArea;
        int x = position is HudPosition.TopRight or HudPosition.BottomRight ? bounds.Right - width - margin : bounds.Left + margin;
        int y = position is HudPosition.BottomLeft or HudPosition.BottomRight ? bounds.Bottom - height - margin : bounds.Top + margin;
        _ = SetWindowPos(handle, TopmostLayer, x, y, 0, 0, PositionWithoutResizeOrActivation);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public ScreenRect DisplayArea, WorkArea;
        public uint Flags;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLong(nint window, int index);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true)]
    private static extern int SetWindowLong(nint window, int index, int value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(nint window, out ScreenRect rectangle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
