using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WPM.App.Benchmarks;

public partial class BenchmarkPage : UserControl
{
    private enum Kind { CpuMulti, CpuSingle, GameScene, RayTracing, Simulation, Memory, Map, Disk, Stress }

    private sealed record Option(Kind Kind, string Label, string Method);
    private sealed record Measurement(string Label, string Value);
    private sealed record Outcome(Measurement[] Items, int[]? Pixels = null);
    private sealed record RunningStatus(string Text, double? Progress);

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private readonly DispatcherTimer timer;
    private CancellationTokenSource? cancellation;
    private Func<RunningStatus>? currentProgress;
    private RunningStatus state = new("Preparando o teste…", null);
    private string folder = Path.GetTempPath();

    public BenchmarkPage()
    {
        InitializeComponent();
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += Timer_Tick;
        StoragePath.Text = folder;
        Workload.ItemsSource = new Option[]
        {
            new(Kind.CpuMulti, "Renderização CPU · múltiplas threads", "Renderiza uma imagem de 640 × 360 com 96 amostras por pixel. Usa os processadores lógicos disponíveis."),
            new(Kind.CpuSingle, "Renderização CPU · uma thread", "Renderiza a mesma imagem com uma thread, para medir o desempenho de uma tarefa isolada."),
            new(Kind.GameScene, "Cena de jogo · GPU", "Renderiza uma cena 3D sintética por 20 segundos em Direct3D 11. O teste abre uma janela própria."),
            new(Kind.RayTracing, "Ray tracing · GPU", "Renderização com Direct3D 12 e DXR 1.1. Mede três passadas de 8 segundos após o aquecimento."),
            new(Kind.Simulation, "Simulação de jogo · CPU", "Calcula física, colisões e IA de 30 mil unidades durante 10 segundos."),
            new(Kind.Memory, "Memória RAM", "Mede latência, leitura e cópia com uma sequência de dados de 128 MiB. O teste usa uma tarefa."),
            new(Kind.Map, "Carregamento de mapa", "Lê um pacote de 128 MiB e 128 trechos de 64 KiB de um arquivo temporário, sem o cache de arquivos do Windows."),
            new(Kind.Disk, "Leitura e gravação · Disco", "Usa um arquivo temporário de 128 MiB para medir acesso sequencial e aleatório de 4 KiB sem o cache de arquivos do Windows."),
            new(Kind.Stress, "Teste de estresse · CPU", "Executa e verifica cálculos em paralelo por 15 segundos, com até 64 threads."),
        };
        Workload.SelectedIndex = 0;
    }

    private void Workload_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Workload.SelectedItem is not Option option)
        {
            return;
        }

        Method.Text = option.Method;
        GpuControls.Visibility = option.Kind is Kind.GameScene or Kind.RayTracing ? Visibility.Visible : Visibility.Collapsed;
        StorageControls.Visibility = option.Kind is Kind.Disk or Kind.Map ? Visibility.Visible : Visibility.Collapsed;
        Result.Visibility = Visibility.Collapsed;
        BenchmarkStatus.Visibility = Visibility.Collapsed;
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pasta para a amostra temporária", InitialDirectory = folder };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            folder = dialog.FolderName;
            StoragePath.Text = folder;
        }
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null || Workload.SelectedItem is not Option option)
        {
            return;
        }

        (int width, int height) = Resolution.SelectedIndex == 1 ? (1920, 1080) : (1280, 720);
        var storageFolder = folder;
        using var source = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        cancellation = source;
        SetRunning(true);
        Result.Visibility = Visibility.Collapsed;
        RenderPreview.Source = null;
        currentProgress = null;
        SetState("Preparando o teste…", null);
        BenchmarkStatus.Visibility = Visibility.Visible;
        Timer_Tick(this, EventArgs.Empty);
        timer.Start();
        try
        {
            var outcome = await Task.Run(() => Execute(option.Kind, width, height, storageFolder, source.Token), source.Token);
            source.Token.ThrowIfCancellationRequested();
            ResultTitle.Text = option.Label;
            Measurements.ItemsSource = outcome.Items;
            RenderPreview.Visibility = outcome.Pixels is null ? Visibility.Collapsed : Visibility.Visible;
            if (outcome.Pixels is { } pixels)
            {
                RenderPreview.Source = BitmapSource.Create(BenchRenderer.Width, BenchRenderer.Height, 96, 96,
                    PixelFormats.Bgra32, null, pixels, BenchRenderer.Width * sizeof(int));
            }
            Result.Visibility = Visibility.Visible;
            Status.Text = "Benchmark concluído.";
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Benchmark interrompido.";
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            Status.Text = $"Não foi possível concluir o benchmark. {error.GetBaseException().Message}";
        }
        finally
        {
            timer.Stop();
            currentProgress = null;
            ProgressBar.Visibility = Visibility.Collapsed;
            cancellation = null;
            SetRunning(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Page_Unloaded(object sender, RoutedEventArgs e) => Cancel();

    private void Cancel()
    {
        if (cancellation is { IsCancellationRequested: false })
        {
            CancelButton.IsEnabled = false;
            cancellation.Cancel();
        }
    }

    private void SetRunning(bool running)
    {
        Workload.IsEnabled = !running;
        Resolution.IsEnabled = !running;
        ChangeFolder.IsEnabled = !running;
        RunButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var progress = cancellation?.IsCancellationRequested == true
            ? new RunningStatus("Interrompendo a medição…", null)
            : Volatile.Read(ref currentProgress)?.Invoke() ?? Volatile.Read(ref state);
        if (Status.Text != progress.Text) Status.Text = progress.Text;
        ProgressBar.IsIndeterminate = !progress.Progress.HasValue;
        ProgressBar.Value = Math.Clamp(progress.Progress.GetValueOrDefault(), 0, 1) * 100;
    }

    private void SetState(string text, double? progress) => Volatile.Write(ref state, new RunningStatus(text, progress));

    private Outcome Execute(Kind kind, int width, int height, string storageFolder, CancellationToken token)
    {
        switch (kind)
        {
            case Kind.CpuMulti:
            case Kind.CpuSingle:
                var renderer = new BenchRenderer();
                Volatile.Write(ref currentProgress, () => new RunningStatus("Renderizando a cena na CPU",
                    renderer.BucketsDone / (double)renderer.Buckets.Count));
                var cpu = renderer.Run(kind == Kind.CpuSingle ? 1 : Environment.ProcessorCount, TimeSpan.Zero, token);
                return new Outcome(
                [
                    new("Pontuação", Format(cpu.Score)),
                    new("Tempo de renderização", Format(cpu.Seconds, "s")),
                    new("Threads", cpu.Threads.ToString(PtBr)),
                    new("Amostras calculadas", cpu.Samples.ToString("N0", PtBr))
                ], renderer.Pixels);

            case Kind.GameScene:
                var scene = new GpuGameBench { Width = width, Height = height, Duration = TimeSpan.FromSeconds(20) };
                using (token.Register(scene.Cancel))
                {
                    Volatile.Write(ref currentProgress, () => new RunningStatus("Renderizando a cena de jogo", scene.Progress));
                    var result = scene.Run();
                    if (scene.Cancelled) throw new OperationCanceledException();
                    if (result is null || result.Frames <= 0)
                        throw new InvalidOperationException(scene.Error ?? "A GPU não retornou quadros válidos em Direct3D 11.");
                    return GraphicsOutcome(result.Adapter, result.Width, result.Height, result.AvgFps, result.Low1Fps, result.Score);
                }

            case Kind.RayTracing:
                SetState("Verificando suporte a Direct3D 12 e DXR 1.1…", null);
                string? capability = GpuRtBench.CheckSupport("");
                if (capability is not null) throw new InvalidOperationException(capability);
                token.ThrowIfCancellationRequested();
                var tracing = new GpuRtBench
                {
                    Width = width, Height = height, Preset = "normal",
                    PassDuration = TimeSpan.FromSeconds(8), Passes = 3, Warmup = TimeSpan.FromSeconds(3)
                };
                using (token.Register(tracing.Cancel))
                {
                    Volatile.Write(ref currentProgress, () => new RunningStatus(
                        string.IsNullOrWhiteSpace(tracing.Phase) ? "Preparando a cena de ray tracing…" : tracing.Phase, tracing.Progress));
                    var result = tracing.Run();
                    if (tracing.Cancelled) throw new OperationCanceledException();
                    if (result is null || result.Passes.Count != tracing.Passes || result.Passes.Any(pass => pass.Frames <= 0))
                        throw new InvalidOperationException(tracing.Error ?? "A GPU não retornou quadros válidos em DXR.");
                    var graphics = GraphicsOutcome(result.Adapter, result.Width, result.Height,
                        result.MedianFps, result.MedianLow1, result.Score, "FPS médios · mediana das passadas");
                    return new Outcome([.. graphics.Items, new("Variação entre passadas", Format(result.VariationPct, "%"))]);
                }

            case Kind.Simulation:
                var simulation = new GameSimBench();
                Volatile.Write(ref currentProgress, () => new RunningStatus("Simulando unidades na CPU", simulation.Progress));
                var game = simulation.Run(TimeSpan.FromSeconds(10), token);
                return new Outcome(
                [
                    new("Quadros simulados por segundo", Format(game.AvgFps)),
                    new("FPS equivalente ao percentil 99", Format(game.Low1Fps)),
                    new("Tempo médio · tarefas em série", Format(game.MainThreadMs, "ms")),
                    new("Tempo médio · tarefas em paralelo", Format(game.ParallelMs, "ms"))
                ]);

            case Kind.Memory:
                var memory = LocalBenchmarks.MeasureMemory((text, progress) => SetState(text, progress), token);
                return new Outcome(
                [
                    new("Latência média de acesso", Format(memory.LatencyNs, "ns")),
                    new("Leitura sequencial", Format(memory.ReadGiBs, "GiB/s")),
                    new("Cópia sequencial", Format(memory.CopyGiBs, "GiB/s"))
                ]);

            case Kind.Map:
                var map = LocalBenchmarks.MeasureMap(storageFolder, (text, progress) => SetState(text, progress), token);
                return new Outcome(
                [
                    new("Leitura do pacote", Format(map.PackSeconds, "s")),
                    new("Leitura dos trechos de recursos", Format(map.AssetsSeconds, "s")),
                    new("Tempo total de carregamento simulado", Format(map.PackSeconds + map.AssetsSeconds, "s"))
                ]);

            case Kind.Disk:
                var disk = LocalBenchmarks.MeasureDisk(storageFolder, (text, progress) => SetState(text, progress), token);
                return new Outcome(
                [
                    new("Leitura sequencial", Format(disk.SequentialRead, "MiB/s")),
                    new("Gravação sequencial", Format(disk.SequentialWrite, "MiB/s")),
                    new("Leitura aleatória de 4 KiB", Format(disk.RandomRead, "IOPS")),
                    new("Gravação aleatória de 4 KiB", Format(disk.RandomWrite, "IOPS"))
                ]);

            case Kind.Stress:
                var timer = Stopwatch.StartNew();
                Volatile.Write(ref currentProgress, () => new RunningStatus("Verificando cálculos em paralelo",
                    timer.Elapsed.TotalSeconds / LocalBenchmarks.StressSeconds));
                var stress = LocalBenchmarks.StressProcessor(token);
                return new Outcome(
                [
                    new("Duração", Format(stress.Elapsed.TotalSeconds, "s")),
                    new("Blocos SHA-256 verificados", stress.Blocks.ToString("N0", PtBr))
                ]);

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static Outcome GraphicsOutcome(string gpu, int width, int height, double fps, double fpsP99, double score,
        string label = "FPS médio") => new(
    [
        new("Adaptador gráfico", gpu),
        new("Resolução", $"{width} × {height}"),
        new(label, Format(fps)),
        new("FPS equivalente ao percentil 99", Format(fpsP99)),
        new("Pontuação", Format(score))
    ]);

    private static string Format(double value, string unit = "")
    {
        string formatted = value.ToString("0.##", PtBr);
        return unit.Length == 0 ? formatted : $"{formatted} {unit}";
    }
}
