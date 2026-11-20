using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace WPM.App.Monitoring;

public enum MonitoringSection { Sensors, Bottleneck, Overlay }

public partial class MonitoringPage : UserControl
{
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(nameof(Section),
        typeof(MonitoringSection), typeof(MonitoringPage),
        new PropertyMetadata(MonitoringSection.Sensors, (target, _) => ((MonitoringPage)target).ShowSection()));

    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("pt-BR");
    private const string AllSensors = "Todos os tipos";
    private readonly ObservableCollection<SensorTableRow> rows = [];
    private readonly Dictionary<string, SensorTableRow> rowsById = new(StringComparer.Ordinal);
    private readonly ICollectionView sensorView;
    private SensorSample? displayedSample;
    private MonitoringHub? hub;

    public MonitoringSection Section
    {
        get => (MonitoringSection)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    public MonitoringPage()
    {
        InitializeComponent();
        SensorFilter.ItemsSource = new[] { AllSensors }.Concat(Enum.GetValues<SensorType>()
            .Select(SensorDisplay.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Create(DisplayCulture, false)));
        SensorFilter.SelectedIndex = 0;
        sensorView = CollectionViewSource.GetDefaultView(rows);
        sensorView.Filter = IncludeSensor;
        SensorTable.ItemsSource = sensorView;
        SensorFilter.SelectionChanged += (_, _) => sensorView.Refresh();
        AvailableOnly.Checked += (_, _) => sensorView.Refresh();
        AvailableOnly.Unchecked += (_, _) => sensorView.Refresh();
        HudCorner.SelectionChanged += (_, _) => ApplyHudOptions();
        HudTint.SelectionChanged += (_, _) => ApplyHudOptions();
        foreach (CheckBox option in HudMetricsOptions.Children.OfType<CheckBox>())
        {
            option.Checked += (_, _) => ApplyHudOptions();
            option.Unchecked += (_, _) => ApplyHudOptions();
        }

        Loaded += (_, _) =>
        {
            if (hub is null)
            {
                hub = MonitoringHub.Shared;
                hub.Updated += RefreshPage;
            }
            ShowSection();
        };
        Unloaded += (_, _) =>
        {
            if (hub is not null)
            {
                hub.Updated -= RefreshPage;
                hub.SetPreview(this, false);
                hub = null;
            }
        };
        IsVisibleChanged += (_, _) => UpdatePreviewDemand();
        ShowSection();
    }

    private void ShowSection()
    {
        if (SensorsPanel is null)
        {
            return;
        }
        SensorsPanel.Visibility = Section == MonitoringSection.Sensors ? Visibility.Visible : Visibility.Collapsed;
        BottleneckPanel.Visibility = Section == MonitoringSection.Bottleneck ? Visibility.Visible : Visibility.Collapsed;
        OverlayPanel.Visibility = Section == MonitoringSection.Overlay ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreviewDemand();
        RefreshPage(this, EventArgs.Empty);
    }

    private void UpdatePreviewDemand() =>
        hub?.SetPreview(this, IsLoaded && IsVisible && Section is (MonitoringSection.Sensors or MonitoringSection.Bottleneck));

    private bool IncludeSensor(object item)
    {
        return item is SensorTableRow sensor && (SensorFilter.SelectedItem is null ||
            SensorFilter.SelectedItem is AllSensors || Equals(SensorFilter.SelectedItem, sensor.Category)) &&
            (AvailableOnly.IsChecked != true || sensor.Value.HasValue);
    }

    private void RefreshPage(object? sender, EventArgs args)
    {
        if (hub is null)
        {
            return;
        }

        switch (Section)
        {
            case MonitoringSection.Sensors when hub.Sample is { } sample && !ReferenceEquals(sample, displayedSample):
                ShowSensors(sample);
                break;
            case MonitoringSection.Bottleneck:
                ShowSession();
                break;
            case MonitoringSection.Overlay:
                ShowHudStatus();
                break;
        }
    }

    private void ShowSensors(SensorSample sample)
    {
        displayedSample = sample;
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        bool refreshAvailability = false;
        foreach (var reading in sample.Readings)
        {
            if (!activeIds.Add(reading.Id))
            {
                continue;
            }

            if (!rowsById.TryGetValue(reading.Id, out var row))
            {
                row = new SensorTableRow(reading);
                rowsById.Add(reading.Id, row);
                rows.Add(row);
            }
            else
            {
                refreshAvailability |= row.Value.HasValue != reading.Value.HasValue;
                row.Update(reading);
            }
        }

        foreach (string removed in rowsById.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            rows.Remove(rowsById[removed]);
            rowsById.Remove(removed);
        }

        if (AvailableOnly.IsChecked == true && refreshAvailability)
        {
            sensorView.Refresh();
        }
        int current = rows.Count(row => row.Value.HasValue);
        SensorsStatus.Text = sample.LibraryAvailable
            ? $"{current} de {rows.Count} sensores com leitura · {DateTime.Now:HH:mm:ss}. Alguns componentes exigem um driver de sensores compatível ou permissões adicionais."
            : "Leituras fornecidas pelos contadores do Windows. A biblioteca de sensores está indisponível.";
    }

    private void StartSession_Click(object sender, RoutedEventArgs args)
    {
        if (Duration.SelectedItem is ComboBoxItem { Tag: string value } &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
        {
            hub?.StartSession(minutes, SessionFrames.IsChecked == true);
        }
    }

    private void EndSession_Click(object sender, RoutedEventArgs args) => hub?.EndSession();

    private void ShowSession()
    {
        if (hub is null)
        {
            return;
        }
        var session = hub.Session;
        bool running = session?.IsRunning == true;
        StartSessionButton.IsEnabled = Duration.IsEnabled = SessionFrames.IsEnabled = !running;
        EndSessionButton.IsEnabled = running;
        SessionProgress.Value = session is null ? 0 :
            Math.Min(100, 100 * session.Elapsed.TotalSeconds / session.Duration.TotalSeconds);
        if (session is not null)
        {
            string state = running ? "Em andamento" : session.Completed ? "Concluída" : "Encerrada";
            SessionStatus.Text = $"{state} · {session.Elapsed:mm\\:ss} de {session.Duration:mm\\:ss} · {session.Count} amostras";
            SessionResults.ItemsSource = session.Results();
        }
        if (running && session?.CaptureFrames == true)
        {
            SessionFrameStatus.Text = hub.FrameStatus;
        }
        else
        {
            SessionFrameStatus.Text = "";
        }

        if (hub.Sample is { } sample)
        {
            CurrentAssessment.Text = MonitoringSession.Label(MonitoringSession.Assess(sample));
            LiveReadings.ItemsSource = MonitoringSession.CurrentReadings(sample, hub.Frames);
        }
    }

    private void HudToggle_Click(object sender, RoutedEventArgs args)
    {
        if (hub?.HudActive == true)
        {
            hub.HideHud();
        }
        else if (ReadHudOptions() is { Metrics: not HudMetrics.None } options)
        {
            hub?.ShowHud(options);
        }
    }

    private void ApplyHudOptions()
    {
        if (Section == MonitoringSection.Overlay && hub is not null)
        {
            var options = ReadHudOptions();
            if (options.Metrics == HudMetrics.None && hub.HudActive)
            {
                hub.HideHud();
            }
            else
            {
                hub.UpdateHud(options);
            }
            ShowHudStatus();
        }
    }

    private HudOptions ReadHudOptions()
    {
        var metrics = (HudCpu.IsChecked == true ? HudMetrics.Processor : HudMetrics.None) |
            (HudGpu.IsChecked == true ? HudMetrics.Graphics : HudMetrics.None) |
            (HudMemory.IsChecked == true ? HudMetrics.Memory : HudMetrics.None) |
            (HudTemperatures.IsChecked == true ? HudMetrics.Temperatures : HudMetrics.None) |
            (HudFps.IsChecked == true ? HudMetrics.Fps : HudMetrics.None) |
            (HudFrameTimes.IsChecked == true ? HudMetrics.FrameTimes : HudMetrics.None) |
            (HudClocks.IsChecked == true ? HudMetrics.Clocks : HudMetrics.None) |
            (HudPower.IsChecked == true ? HudMetrics.Power : HudMetrics.None);
        var color = HudTint.SelectedItem is ComboBoxItem { Tag: string tint }
            ? (Color)ColorConverter.ConvertFromString(tint) : Colors.White;
        return new((HudPosition)Math.Clamp(HudCorner.SelectedIndex, 0, 3), color, metrics);
    }

    private void ShowHudStatus()
    {
        bool active = hub?.HudActive == true;
        var options = ReadHudOptions();
        HudToggle.Content = active ? "Ocultar HUD" : "Mostrar HUD";
        HudToggle.IsEnabled = active || options.Metrics != HudMetrics.None;
        HudStatus.Text = active ? "HUD visível. A posição acompanha o monitor da janela em primeiro plano."
            : options.Metrics == HudMetrics.None ? "Selecione pelo menos um indicador." : "HUD oculto.";
        HudFrameStatus.Text = active && options.NeedsFrames ? hub?.FrameStatus : "";
    }
}

internal sealed class SensorTableRow : INotifyPropertyChanged
{
    private SensorReading reading;
    private double? min;
    private double? max;

    public SensorTableRow(SensorReading reading)
    {
        this.reading = reading;
        min = reading.Minimum ?? reading.Value;
        max = reading.Maximum ?? reading.Value;
    }

    public string Device => reading.Device;
    public string Name => reading.Name;
    public string Category => SensorDisplay.Category(reading.Type);
    public double? Value => reading.Value;
    public string Current => SensorDisplay.Value(reading.Type, reading.Value);
    public string Minimum => SensorDisplay.Value(reading.Type, min);
    public string Maximum => SensorDisplay.Value(reading.Type, max);
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(SensorReading value)
    {
        if (value == reading)
        {
            return;
        }

        reading = value;
        if (value.Value is double latest)
        {
            min = value.Minimum ?? Math.Min(min ?? latest, latest);
            max = value.Maximum ?? Math.Max(max ?? latest, latest);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
