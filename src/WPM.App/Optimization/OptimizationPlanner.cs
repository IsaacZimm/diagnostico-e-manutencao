namespace WPM.App.Optimization;

internal enum OptimizationResource { Cpu, Memory, Disk, Gpu }

internal enum OptimizationActionKind { LowerPriority, RequestClose }

internal sealed record OptimizationAction(
    OptimizationActionKind Kind,
    OptimizationProcess Process,
    string Title,
    string Evidence,
    string Impact);

internal sealed record ResourceConsumer(string Name, string Measurement);

internal sealed record OptimizationCard(
    OptimizationResource Resource,
    string Title,
    double? Percent,
    string Detail,
    string ConsumerHeading,
    IReadOnlyList<ResourceConsumer> Consumers,
    bool IsHigh,
    string Guidance,
    IReadOnlyList<OptimizationAction> Actions)
{
    public bool IsHighestUsage { get; init; }
    public bool HasActions => Actions.Count > 0;
    public bool HasConsumers => Consumers.Count > 0;
    public bool HasMeasurement => Percent.HasValue;
    public double BarValue => Percent ?? 0;
    public string Value => Percent is { } value ? $"{value:0.#}%" : "—";
    public string Status => IsHigh ? "Uso elevado" : HasMeasurement ? "Uso normal" : "Sem leitura";
}

internal static class OptimizationPlanner
{
    private const double CpuThreshold = 50;
    private const double MemoryThreshold = 85;
    private const double DiskThreshold = 85;
    private const double GpuThreshold = 90;
    private const double CpuCandidateThreshold = 2;
    private const long MinimumResidentBytes = 250 * 1024 * 1024;
    private const int TopProcessCount = 3;

    public static IReadOnlyList<OptimizationCard> Create(OptimizationSnapshot snapshot)
    {
        OptimizationCard[] cards = [DescribeCpu(snapshot), DescribeMemory(snapshot), DescribeDisk(snapshot), DescribeGpu(snapshot)];
        double? highestUsage = cards.Max(card => card.Percent);
        return highestUsage is > 0
            ? cards.Select(card => card.Percent == highestUsage ? card with { IsHighestUsage = true } : card).ToArray()
            : cards;
    }

    public static bool CanApply(OptimizationResource resource, OptimizationActionKind kind, OptimizationProcess process)
    {
        if (!process.CanAdjust || process.StartTimeUtcTicks <= 0
            || process.Kind is not (OptimizationAppKind.Browser or OptimizationAppKind.Discretionary))
        {
            return false;
        }

        return (resource, kind) switch
        {
            (OptimizationResource.Cpu, OptimizationActionKind.LowerPriority) => process.IsNormalPriority,
            (OptimizationResource.Memory or OptimizationResource.Gpu, OptimizationActionKind.RequestClose) =>
                process.Kind == OptimizationAppKind.Browser || process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static OptimizationCard DescribeCpu(OptimizationSnapshot snapshot)
    {
        var ranked = Rank(snapshot.Processes, process => process.CpuPercent);
        bool systemBusy = snapshot.CpuPercent >= CpuThreshold;
        double coreEquivalent = 90d / Math.Max(snapshot.LogicalProcessorCount, 1);
        var heavy = ranked.Where(process => process.CpuPercent >= coreEquivalent).ToArray();
        bool processBusy = snapshot.LogicalProcessorCount > 0 && heavy.Length > 0;
        bool high = systemBusy || processBusy;
        var actions = (systemBusy ? ranked.Where(process => process.CpuPercent >= CpuCandidateThreshold) : processBusy ? heavy : [])
            .Where(process => CanApply(OptimizationResource.Cpu, OptimizationActionKind.LowerPriority, process))
            .Take(TopProcessCount).Select(process => new OptimizationAction(OptimizationActionKind.LowerPriority, process,
                $"Reduzir a prioridade de {process.AppName}", $"{NameOf(process)} · {process.CpuPercent:0.#}% da CPU total.",
                "Passa para Abaixo do normal. Novos subprocessos podem herdar a redução. A prioridade deste processo pode ser restaurada.")).ToArray();

        string detail = snapshot.CpuPercent.HasValue ? "CPU total na amostra." : "O Windows não forneceu o uso total da CPU.";
        string guidance = !systemBusy && processBusy
            ? "Um processo usou CPU equivalente a pelo menos 90% de um processador lógico, mesmo com uso total abaixo do limite."
            : "";
        if (high && actions.Length == 0)
        {
            guidance += (guidance.Length > 0 ? " " : "") + "Nenhum processo com carga alta é elegível para ajuste automático.";
        }

        return new(OptimizationResource.Cpu, "Processador", snapshot.CpuPercent, detail, "Maior uso de CPU",
            Consumers(ranked, process => $"{process.CpuPercent:0.#}%"), high, guidance, actions);
    }

    private static OptimizationCard DescribeMemory(OptimizationSnapshot snapshot)
    {
        double? percent = snapshot.TotalBytes is > 0 && snapshot.AvailableBytes is >= 0 && snapshot.AvailableBytes <= snapshot.TotalBytes
            ? 100d * (snapshot.TotalBytes.Value - snapshot.AvailableBytes.Value) / snapshot.TotalBytes.Value
            : null;
        string detail = percent.HasValue
            ? $"{FormatBytes(snapshot.AvailableBytes!.Value)} disponíveis de {FormatBytes(snapshot.TotalBytes!.Value)}."
            : "O Windows não forneceu o uso da RAM.";
        bool high = percent >= MemoryThreshold;
        var ranked = Rank(snapshot.Processes, process => process.WorkingSetBytes);
        var actions = high ? ranked.Where(process => process.WorkingSetBytes >= MinimumResidentBytes
                && CanApply(OptimizationResource.Memory, OptimizationActionKind.RequestClose, process))
            .Take(TopProcessCount).Select(process => CloseAction(process, $"{FormatBytes(process.WorkingSetBytes!.Value)} de RAM residente.")).ToArray() : [];

        return new(OptimizationResource.Memory, "Memória RAM", percent, detail, "Maior RAM residente por processo",
            Consumers(ranked, process => FormatBytes(process.WorkingSetBytes!.Value)), high,
            high && actions.Length == 0 ? "Nenhum aplicativo com consumo relevante é elegível para fechamento automático." : "", actions);
    }

    private static OptimizationCard DescribeDisk(OptimizationSnapshot snapshot)
    {
        var disk = snapshot.Disk;
        bool high = disk?.Utilization >= DiskThreshold;
        string detail = disk is null ? "O Windows não forneceu o tempo ativo do armazenamento."
            : $"Tempo ativo · {disk.Name}" + (disk.BytesPerSecond is { } speed ? $" · {FormatBytes(speed)}/s" : "");
        var ranked = Rank(snapshot.Processes, process => process.IoBytesPerSecond);

        return new(OptimizationResource.Disk, "SSD / Disco", disk?.Utilization, detail, "Maior E/S geral por processo",
            Consumers(ranked, process => $"{FormatBytes(process.IoBytesPerSecond!.Value)}/s"), high,
            "A E/S de cada processo inclui rede e dispositivos. A leitura não atribui o uso ao disco exibido.", []);
    }

    private static OptimizationCard DescribeGpu(OptimizationSnapshot snapshot)
    {
        var gpu = snapshot.Gpu;
        bool high = gpu?.Utilization >= GpuThreshold;
        var contributors = high ? gpu?.BusiestEngineProcesses : null;
        var readings = contributors ?? gpu?.Processes;
        var byId = snapshot.Processes.ToDictionary(process => process.Id);
        var ranked = readings?.Where(item => item.Value > 0)
            .OrderByDescending(item => item.Value).ThenBy(item => item.Key).ToArray() ?? [];
        var consumers = ranked.Take(TopProcessCount).Select(item => new ResourceConsumer(
            byId.TryGetValue(item.Key, out var process) ? NameOf(process) : $"Processo não identificado · PID {item.Key}",
            $"{item.Value:0.#}%")).ToArray();
        var actions = contributors is null ? [] : ranked.Where(item => item.Value >= 25 && byId.TryGetValue(item.Key, out var process)
                && CanApply(OptimizationResource.Gpu, OptimizationActionKind.RequestClose, process))
            .Take(TopProcessCount).Select(item => CloseAction(byId[item.Key], $"{item.Value:0.#}% no motor mais ocupado da GPU.")).ToArray();

        return new(OptimizationResource.Gpu, "Placa de vídeo", gpu?.Utilization,
            gpu is null ? "Contadores de GPU indisponíveis neste Windows ou driver." : "Uso do motor mais ocupado da GPU.",
            contributors is null ? "Maior uso de GPU por processo" : "Processos no motor mais ocupado", consumers, high,
            high && actions.Length == 0 ? "Nenhum aplicativo com uso relevante é elegível para fechamento automático." : "", actions);
    }

    private static OptimizationAction CloseAction(OptimizationProcess process, string evidence) => new(
        OptimizationActionKind.RequestClose, process, $"Solicitar o fechamento de {process.AppName}",
        $"{NameOf(process)} · {evidence}", "Salve seu trabalho. O aplicativo recebe um pedido normal de fechamento.");

    private static OptimizationProcess[] Rank(IReadOnlyList<OptimizationProcess> processes, Func<OptimizationProcess, double?> value) => processes
        .Where(process => value(process) > 0).OrderByDescending(value).ThenBy(process => process.Id).ToArray();

    private static IReadOnlyList<ResourceConsumer> Consumers(IEnumerable<OptimizationProcess> ranked, Func<OptimizationProcess, string> measurement) =>
        ranked.Take(TopProcessCount).Select(process => new ResourceConsumer(NameOf(process), measurement(process))).ToArray();

    private static string NameOf(OptimizationProcess process) => $"{process.AppName} · PID {process.Id}";

    private static string FormatBytes(double bytes)
    {
        const double MiB = 1024 * 1024;
        return bytes switch
        {
            >= 1024 * MiB => $"{bytes / (1024 * MiB):0.#} GiB",
            >= MiB => $"{bytes / MiB:0.#} MiB",
            >= 1024 => $"{bytes / 1024:0.#} KiB",
            _ => $"{bytes:0} B"
        };
    }
}
