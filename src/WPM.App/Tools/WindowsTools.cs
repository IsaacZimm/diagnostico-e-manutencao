using System.Diagnostics;
using System.IO;
using WPM.App.Diagnostics;

namespace WPM.App.Tools;

internal sealed record ToolRow(string Name, string Detail, string Location);

internal sealed record ToolInventory(IReadOnlyList<ToolRow> Rows, string? Issue);

internal sealed record AppInstallOption(string Name, string PackageId);

internal static class WindowsTools
{
    public static AppInstallOption[] Catalog { get; } =
    [
        new("7-Zip", "7zip.7zip"),
        new("Discord", "Discord.Discord"),
        new("Google Chrome", "Google.Chrome"),
        new("Steam", "Valve.Steam"),
        new("VLC", "VideoLAN.VLC")
    ];

    public static ToolInventory Startup()
    {
        var rows = new List<ToolRow>();
        string? issue = WmiDiagnosticQuery.Read("SELECT Name, Command, Location FROM Win32_StartupCommand", row =>
        {
            rows.Add(new(Value(row["Name"]), Value(row["Command"]), Value(row["Location"])));
        });
        return Sort(rows, issue);
    }

    public static ToolInventory Drivers()
    {
        var rows = new List<ToolRow>();
        string? issue = WmiDiagnosticQuery.Read(
            "SELECT DeviceName, DriverVersion, DriverProviderName, InfName, IsSigned FROM Win32_PnPSignedDriver", row =>
        {
            if (row["DeviceName"] is not string name || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string signature = row["IsSigned"] switch
            {
                true => "assinado",
                false => "sem assinatura",
                _ => "assinatura indisponível"
            };
            rows.Add(new(name.Trim(), $"{Value(row["DriverProviderName"])} · {Value(row["DriverVersion"])}",
                $"{Value(row["InfName"])} · {signature}"));
        });
        return Sort(rows, issue);
    }

    public static string WinGetPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", "winget.exe");

    public static async Task<int> InstallAsync(AppInstallOption option)
    {
        var info = new ProcessStartInfo(WinGetPath) { UseShellExecute = false };
        foreach (string argument in new[] { "install", "--id", option.PackageId, "--exact", "--source", "winget", "--interactive" })
        {
            info.ArgumentList.Add(argument);
        }

        using Process installer = Process.Start(info) ?? throw new InvalidOperationException("O WinGet não abriu.");
        await installer.WaitForExitAsync();
        return installer.ExitCode;
    }

    private static ToolInventory Sort(List<ToolRow> rows, string? issue) =>
        new(rows.Distinct().OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), issue);

    private static string Value(object? value) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : "Indisponível";
}
