using System.IO;

namespace WPM.App.Repair;

internal enum RepairOperation
{
    CheckHealth,
    ScanHealth,
    RestoreHealth,
    AnalyzeComponentStore,
    StartComponentCleanup,
    ScanSystemFiles
}

internal sealed record RepairCommand(
    RepairOperation Operation,
    string Title,
    string Description,
    string CommandLine,
    string LogFile,
    bool ModifiesWindows);

internal static class RepairCommands
{
    private static readonly string LogsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs");

    public static IReadOnlyList<RepairCommand> All { get; } = Array.AsReadOnly<RepairCommand>(
    [
        Dism(RepairOperation.CheckHealth, "Consultar integridade",
            "Consulta se o Windows já registrou corrupção na imagem e se ela pode ser reparada."),
        Dism(RepairOperation.ScanHealth, "Verificar a imagem",
            "Examina o repositório de componentes do Windows em busca de corrupção."),
        Dism(RepairOperation.RestoreHealth, "Reparar a imagem",
            "Examina e repara a imagem do Windows. Pode buscar arquivos de reparo no Windows Update.", modifiesWindows: true),
        Dism(RepairOperation.AnalyzeComponentStore, "Analisar componentes",
            "Calcula o tamanho do repositório de componentes do Windows (WinSxS) e informa se a limpeza é recomendada."),
        Dism(RepairOperation.StartComponentCleanup, "Limpar componentes antigos",
            "Remove versões substituídas de componentes do Windows no repositório WinSxS.", modifiesWindows: true),
        new(RepairOperation.ScanSystemFiles, "Verificar arquivos do sistema",
            "Verifica os arquivos protegidos do Windows e substitui versões incorretas quando possível.",
            FormatCommand(RepairOperation.ScanSystemFiles, "sfc"), Path.Combine(LogsDirectory, "CBS", "CBS.log"), true)
    ]);

    public static RepairCommand Get(RepairOperation operation)
    {
        return All.FirstOrDefault(command => command.Operation == operation)
            ?? throw new ArgumentOutOfRangeException(nameof(operation));
    }

    internal static string[] GetArguments(RepairOperation operation)
    {
        return operation switch
        {
            RepairOperation.ScanSystemFiles => ["/scannow"],
            RepairOperation.CheckHealth or RepairOperation.ScanHealth or RepairOperation.RestoreHealth
                or RepairOperation.AnalyzeComponentStore or RepairOperation.StartComponentCleanup
                => ["/Online", "/NoRestart", "/Cleanup-Image", $"/{operation}"],
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static RepairCommand Dism(RepairOperation operation, string title, string description, bool modifiesWindows = false)
    {
        return new RepairCommand(operation, title, description, FormatCommand(operation, "DISM"),
            Path.Combine(LogsDirectory, "DISM", "dism.log"), modifiesWindows);
    }

    private static string FormatCommand(RepairOperation operation, string executable)
    {
        return $"{executable} {string.Join(' ', GetArguments(operation))}";
    }
}
