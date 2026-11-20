using System.Windows;
using System.Windows.Controls;

namespace WPM.App.Games;

public partial class GamesPage : UserControl
{
    private GameHardware? hardware;
    private bool initialized;

    public bool ShowEstimates { get; set; }

    public GamesPage() => InitializeComponent();

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        PageHeading.Text = ShowEstimates ? "FPS estimado" : "Biblioteca de jogos";
        LibraryPanel.Visibility = ShowEstimates ? Visibility.Collapsed : Visibility.Visible;
        EstimatesPanel.Visibility = ShowEstimates ? Visibility.Visible : Visibility.Collapsed;
        if (!ShowEstimates)
        {
            await ScanLibraryAsync();
            return;
        }

        hardware = await Task.Run(InstalledGames.ReadHardware);
        ProcessorText.Text = hardware.Processor ?? "Processador não identificado";
        GpuSelector.ItemsSource = hardware.Graphics;
        GpuSelector.SelectedItem = hardware.Graphics.FirstOrDefault(gpu => GameCatalog.GpuPerf(gpu) is not null)
            ?? hardware.Graphics.FirstOrDefault();
        ResolutionSelector.SelectedIndex = 0;
        PresetSelector.SelectedIndex = 2;
        ShowFpsEstimates();
    }

    private async void ScanLibrary_Click(object sender, RoutedEventArgs e) => await ScanLibraryAsync();

    private async Task ScanLibraryAsync()
    {
        ScanLibraryButton.IsEnabled = false;
        LibraryStatus.Text = "Consultando Steam, Epic Games, GOG e Ubisoft Connect…";
        try
        {
            var games = await Task.Run(InstalledGames.Scan);
            LibraryGrid.ItemsSource = games;
            LibraryGrid.Visibility = games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LibraryStatus.Text = games.Count switch
            {
                0 => "Nenhum jogo localizado nas pastas e manifestos das lojas disponíveis.",
                1 => "1 jogo instalado localizado.",
                _ => $"{games.Count} jogos instalados localizados."
            };
        }
        finally
        {
            ScanLibraryButton.IsEnabled = true;
        }
    }

    private void EstimateOption_Changed(object sender, SelectionChangedEventArgs e) => ShowFpsEstimates();

    private void ShowFpsEstimates()
    {
        if (hardware is null || ResolutionSelector.SelectedItem is not ComboBoxItem { Tag: string resolution }
            || PresetSelector.SelectedItem is not ComboBoxItem { Content: string preset })
        {
            return;
        }

        if (hardware.Processor is not { } cpu || GpuSelector.SelectedItem is not string gpu
            || GameCatalog.CpuPerf(cpu) is not double cpuIndex || GameCatalog.GpuPerf(gpu) is not double gpuIndex)
        {
            EstimateGrid.Visibility = Visibility.Collapsed;
            EstimateStatus.Text = "O catálogo local ainda não tem referências suficientes para esta combinação de processador e placa de vídeo.";
            return;
        }

        EstimateStatus.Text = "Valores aproximados do catálogo local. A cena, as atualizações do jogo e o driver podem afetar o FPS.";
        EstimateGrid.ItemsSource = GameCatalog.Games.Select(game =>
        {
            var estimate = GameCatalog.Estimate(game, gpuIndex, cpuIndex, resolution, preset, "");
            string limit = estimate.Limiter switch
            {
                Bottleneck.Gpu => "GPU",
                Bottleneck.Cpu => "Processador",
                Bottleneck.Engine => "Limite do jogo",
                _ => "Indeterminado"
            };
            return new EstimateRow(game.Name, $"≈ {estimate.Avg:0} FPS", limit);
        }).ToArray();
        EstimateGrid.Visibility = Visibility.Visible;
    }

    private sealed record EstimateRow(string Game, string Fps, string Limiter);
}
