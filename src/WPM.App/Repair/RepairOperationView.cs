using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;

namespace WPM.App.Repair;

internal sealed class RepairOperationView(RepairCommand command) : INotifyPropertyChanged
{
    private const int OutputLimit = 48_000;
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("pt-BR");
    private readonly StringBuilder output = new();
    private bool running;
    private bool completed;
    private double? percent;

    public RepairCommand Command { get; } = command;
    public string Status { get; private set; } = "Pronto";
    public string ResultDetail { get; private set; } = "";
    public Visibility ResultVisibility => ResultDetail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public double ProgressValue => completed ? 100 : percent ?? 0;
    public bool IsIndeterminate => running && percent is null;
    public string Output => output.Length == 0 ? "As mensagens desta operação aparecem aqui." : output.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Start()
    {
        output.Clear();
        running = true;
        completed = false;
        percent = null;
        Status = "Preparando a execução…";
        ResultDetail = "";
        Notify();
    }

    public void Apply(RepairUpdate update)
    {
        if (!running)
        {
            return;
        }

        if (update.Percent is >= 0 and <= 100)
        {
            percent = update.Percent;
        }

        if (!string.IsNullOrWhiteSpace(update.Text))
        {
            if (update.IsError)
            {
                output.Append("[stderr] ");
            }

            output.AppendLine(update.Text);
            if (output.Length > OutputLimit)
            {
                output.Remove(0, output.Length - OutputLimit);
            }
        }

        Status = percent is { } value
            ? $"Em execução · {value.ToString("0.#", DisplayCulture)}%"
            : "Em execução · percentual ainda indisponível";
        Notify();
    }

    public void Finish(RepairResult result)
    {
        running = false;
        (Status, ResultDetail, completed) = result switch
        {
            { Cancelled: true } => ("Autorização cancelada", result.Failure ?? "O comando não foi iniciado.", false),
            { ExitCode: 0 } => ("Concluído", "Código 0. Confira a saída para ver o resultado específico da operação.", true),
            { ExitCode: 3010 or unchecked((int)0x80070BC2) } => ("Concluído · reinicialização necessária",
                $"Código {FormatCode(result.ExitCode.Value)}. O Windows informou que a operação exige reinicialização.", true),
            { ExitCode: 1641 or unchecked((int)0x80070669) } => ("Concluído · reinicialização iniciada",
                $"Código {FormatCode(result.ExitCode.Value)}. O Windows informou início da reinicialização.", true),
            { ExitCode: int code } => ("Comando terminou com erro", $"Código {FormatCode(code)}. Confira a saída e o registro do Windows.", false),
            _ => ("Operação indisponível", result.Failure ?? "O resultado do comando não está disponível.", false)
        };
        Notify();
    }

    private static string FormatCode(int code)
    {
        return code < 0 ? $"0x{code:X8}" : code.ToString(CultureInfo.InvariantCulture);
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
