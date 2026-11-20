using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WPM.App.Tools;

public enum ToolsSection { Startup, Apps, Drivers, Lighting }

public partial class ToolsPage : UserControl
{
    private readonly CorsairLighting lighting = new();
    private readonly DispatcherTimer lightingTimer;
    private IReadOnlyList<ToolRow> entries = [];
    private bool initialized;
    private int lastLightingState = -1;
    private string? sdkPath;

    public ToolsSection Section { get; set; }

    public ToolsPage()
    {
        InitializeComponent();
        lightingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        lightingTimer.Tick += LightingTimer_Tick;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        Heading.Text = Section switch
        {
            ToolsSection.Apps => "Instalar aplicativos",
            ToolsSection.Drivers => "Drivers",
            ToolsSection.Lighting => "Iluminação Corsair iCUE",
            _ => "Inicialização do Windows"
        };

        if (Section == ToolsSection.Lighting)
        {
            LightingPanel.Visibility = Visibility.Visible;
            if (Window.GetWindow(this) is { } window)
            {
                window.Closed += (_, _) => CloseLightingSession();
            }

            return;
        }

        if (Section == ToolsSection.Apps)
        {
            AppsPanel.Visibility = Visibility.Visible;
            AppChoice.ItemsSource = WindowsTools.Catalog;
            AppChoice.SelectedIndex = 0;
            return;
        }

        InventoryPanel.Visibility = Visibility.Visible;
        bool drivers = Section == ToolsSection.Drivers;
        SystemButton.Content = drivers ? "Atualizações de driver do Windows" : "Gerenciar no Windows";
        DeviceManagerButton.Visibility = drivers ? Visibility.Visible : Visibility.Collapsed;
        DriverFilterPanel.Visibility = drivers ? Visibility.Visible : Visibility.Collapsed;
        NameColumn.Header = drivers ? "Dispositivo" : "Entrada de inicialização";
        DetailColumn.Header = drivers ? "Fornecedor e versão" : "Comando registrado";
        LocationColumn.Header = drivers ? "Arquivo INF e assinatura" : "Origem";
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        InventoryStatus.Text = "Consultando registros do Windows…";
        try
        {
            var inventory = await Task.Run<ToolInventory>(Section == ToolsSection.Drivers ? WindowsTools.Drivers : WindowsTools.Startup);
            entries = inventory.Rows;
            ApplyFilter();
            InventoryStatus.Text = inventory.Issue ?? (entries.Count == 0 ? "Nenhum item reportado pelo Windows." : $"{entries.Count} itens reportados pelo Windows.");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void DriverFilter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (DriverFilter is null || InventoryGrid is null)
        {
            return;
        }

        string filter = DriverFilter.Text.Trim();
        InventoryGrid.ItemsSource = filter.Length == 0
            ? entries
            : entries.Where(entry => entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || entry.Detail.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || entry.Location.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    private void System_Click(object sender, RoutedEventArgs e) => Open(
        Section == ToolsSection.Drivers ? "ms-settings:windowsupdate-optionalupdates" : "ms-settings:startupapps", InventoryStatus);

    private void DeviceManager_Click(object sender, RoutedEventArgs e) =>
        Open(Path.Combine(Environment.SystemDirectory, "devmgmt.msc"), InventoryStatus);

    private void GetWinGet_Click(object sender, RoutedEventArgs e) =>
        Open("https://learn.microsoft.com/windows/package-manager/winget/", AppsStatus);

    private void GetSdk_Click(object sender, RoutedEventArgs e) =>
        Open("https://github.com/CorsairOfficial/cue-sdk/releases/tag/v4.0.84", LightingStatus);

    private static void Open(string destination, TextBlock status)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            status.Text = "O Windows não conseguiu abrir este destino.";
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (AppChoice.SelectedItem is not AppInstallOption option)
        {
            return;
        }

        if (!File.Exists(WindowsTools.WinGetPath))
        {
            AppsStatus.Text = "WinGet indisponível. Instale ou atualize o Instalador de Aplicativo da Microsoft.";
            return;
        }

        var confirmed = MessageBox.Show(Window.GetWindow(this), $"Abrir o WinGet para instalar {option.Name}?",
            "Instalar aplicativo", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        InstallButton.IsEnabled = false;
        AppChoice.IsEnabled = false;
        AppsStatus.Text = $"Aguardando a instalação de {option.Name}…";
        try
        {
            int exitCode = await WindowsTools.InstallAsync(option);
            AppsStatus.Text = exitCode == 0
                ? $"{option.Name} processado com sucesso pelo WinGet."
                : $"O WinGet encerrou com código 0x{exitCode:X8}. Confira a saída do instalador.";
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            AppsStatus.Text = "O WinGet não conseguiu iniciar a instalação.";
        }
        finally
        {
            AppChoice.IsEnabled = true;
            InstallButton.IsEnabled = true;
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (sdkPath is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Selecionar a DLL oficial do iCUE SDK x64",
                Filter = $"SDK iCUE x64 ({CorsairLighting.DllName})|{CorsairLighting.DllName}",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            {
                return;
            }

            sdkPath = dialog.FileName;
        }

        try
        {
            string? issue = lighting.Connect(sdkPath);
            if (issue is not null)
            {
                LightingStatus.Text = issue;
                sdkPath = null;
                return;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or EntryPointNotFoundException)
        {
            sdkPath = null;
            LightingStatus.Text = "Não foi possível abrir a DLL selecionada.";
            return;
        }

        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = true;
        lastLightingState = -1;
        LightingTimer_Tick(this, EventArgs.Empty);
        lightingTimer.Start();
    }

    private void LightingTimer_Tick(object? sender, EventArgs e)
    {
        int state = lighting.State;
        if (state == lastLightingState)
        {
            return;
        }

        lastLightingState = state;
        LightingControls.IsEnabled = state == CorsairLighting.Connected;
        LightingStatus.Text = state switch
        {
            2 => "Conectando ao iCUE…",
            3 => "O iCUE não respondeu. Verifique se está aberto e com o SDK habilitado.",
            4 => "Conexão recusada. Confira o controle de aplicativos no iCUE.",
            5 => "Conexão com o iCUE interrompida.",
            CorsairLighting.Connected => "Conectado ao iCUE.",
            _ => "Conexão inativa."
        };
        if (state == CorsairLighting.Connected)
        {
            RefreshDevices();
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => CloseLightingSession();

    private void CloseLightingSession()
    {
        lightingTimer.Stop();
        lighting.Disconnect();
        LightingControls.IsEnabled = false;
        LightingDeviceChoice.ItemsSource = null;
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        LightingStatus.Text = "Conexão inativa.";
    }

    private void Devices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        if (lighting.State != CorsairLighting.Connected)
        {
            return;
        }

        var devices = lighting.ReadDevices(out int error);
        LightingDeviceChoice.ItemsSource = devices;
        LightingDeviceChoice.SelectedIndex = devices.Count > 0 ? 0 : -1;
        LightingStatus.Text = error != 0
            ? $"O iCUE não conseguiu listar os dispositivos (código {error})."
            : devices.Count == 0 ? "Nenhum dispositivo com LEDs controláveis foi encontrado." : $"{devices.Count} dispositivos com iluminação encontrados.";
    }

    private void ApplyColor_Click(object sender, RoutedEventArgs e)
    {
        if (lighting.State != CorsairLighting.Connected || LightingDeviceChoice.SelectedItem is not LightingDevice device
            || ColorChoice.SelectedItem is not ComboBoxItem { Tag: string hex })
        {
            return;
        }

        int result = lighting.ApplyColor(device, (Color)ColorConverter.ConvertFromString(hex));
        LightingStatus.Text = result == 0
            ? $"Cor aplicada a {device.Model}. Desconecte para devolver a iluminação ao iCUE."
            : $"O iCUE não aplicou a cor (código {result}). Confira as permissões e as outras integrações de iluminação.";
    }
}
