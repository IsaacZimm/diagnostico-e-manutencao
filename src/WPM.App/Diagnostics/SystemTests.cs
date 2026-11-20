using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;

namespace WPM.App.Diagnostics;

public enum SystemTestKind { Network, Internet, Processor, Disk }

public enum SystemTestStatus { Passed, Unavailable, Failed }

public sealed record SystemTestResult(SystemTestStatus Status, string Summary, IReadOnlyList<DiagnosticItem> Items);

public static class SystemTests
{
    private const int BytesPerMiB = 1024 * 1024;
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("pt-BR");

    public static Task<SystemTestResult> RunAsync(SystemTestKind kind, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return kind switch
            {
                SystemTestKind.Network => await TestNetworkAsync(cancellationToken).ConfigureAwait(false),
                SystemTestKind.Internet => await InternetDiagnostics.RunAsync(cancellationToken).ConfigureAwait(false),
                SystemTestKind.Processor => TestProcessor(cancellationToken),
                SystemTestKind.Disk => await TestDiskAsync(cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }, cancellationToken);
    }

    private static async Task<SystemTestResult> TestNetworkAsync(CancellationToken cancellationToken)
    {
        const int attempts = 3;
        Gateway? gateway;
        try
        {
            gateway = FindGateway(cancellationToken);
        }
        catch (NetworkInformationException)
        {
            return new(SystemTestStatus.Unavailable, "O Windows não disponibilizou a configuração de rede.", []);
        }

        if (gateway is null)
        {
            return new(SystemTestStatus.Unavailable, "Nenhum gateway foi identificado nos adaptadores ativos.", []);
        }

        var items = new List<DiagnosticItem> { new("Adaptador", gateway.Adapter), new("Gateway", gateway.Address.ToString()) };
        var roundTripTimes = new List<long>(attempts);
        try
        {
            using var ping = new Ping();
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                PingReply response = await ping.SendPingAsync(gateway.Address, TimeSpan.FromSeconds(1),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (response.Status == IPStatus.Success)
                {
                    roundTripTimes.Add(response.RoundtripTime);
                }
            }
        }
        catch (PingException)
        {
            return new(SystemTestStatus.Unavailable, "O Windows não conseguiu concluir o teste ICMP.", items);
        }

        cancellationToken.ThrowIfCancellationRequested();
        items.Add(new("Respostas ICMP", $"{roundTripTimes.Count} de {attempts}"));
        double packetLoss = 1d - (double)roundTripTimes.Count / attempts;
        items.Add(new("Tentativas sem resposta", string.Create(DisplayCulture, $"{packetLoss:P0}")));
        if (roundTripTimes.Count == 0)
        {
            return new(SystemTestStatus.Unavailable, "O gateway não respondeu. Ele pode estar configurado para bloquear ping.", items);
        }

        items.Add(new("Tempo médio de resposta", string.Create(DisplayCulture, $"{roundTripTimes.Average():0.#} ms")));
        return new(SystemTestStatus.Passed, "O gateway da rede respondeu ao teste.", items);
    }

    private static Gateway? FindGateway(CancellationToken cancellationToken)
    {
        Gateway? ipv6 = null;
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                IPInterfaceProperties configuration = adapter.GetIPProperties();
                foreach (GatewayIPAddressInformation gateway in configuration.GatewayAddresses)
                {
                    IPAddress address = gateway.Address;
                    if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                    {
                        continue;
                    }

                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return new(adapter.Name, address);
                    }

                    if (ipv6 is null && address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (address.IsIPv6LinkLocal && address.ScopeId == 0)
                        {
                            address = new IPAddress(address.GetAddressBytes(), configuration.GetIPv6Properties().Index);
                        }

                        ipv6 = new(adapter.Name, address);
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // O adaptador pode ter sido desconectado durante a consulta.
            }
        }

        return ipv6;
    }

    private static SystemTestResult TestProcessor(CancellationToken cancellationToken)
    {
        const int blocks = 64;
        var data = new byte[BytesPerMiB];
        // SHA-256 de 1 MiB preenchido com zeros.
        byte[] expected = Convert.FromHexString("30E14955EBF1352266DC2FF8067E68104607E750ABB9D3B36582B8AF909FCB58");
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        var timer = Stopwatch.StartNew();
        for (int block = 0; block < blocks; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SHA256.HashData(data, digest);
            if (!digest.SequenceEqual(expected))
            {
                return new(SystemTestStatus.Failed, "O cálculo SHA-256 retornou um resultado inesperado.", []);
            }
        }

        timer.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        return new(SystemTestStatus.Passed, "O processador concluiu o teste de cálculo SHA-256.",
        [
            new("Volume de dados processado", $"{blocks} MiB"),
            new("Tempo de cálculo", FormatDuration(timer.Elapsed)),
            new("Taxa de cálculo (uma tarefa)", FormatRate(blocks, timer.Elapsed))
        ]);
    }

    private static async Task<SystemTestResult> TestDiskAsync(CancellationToken cancellationToken)
    {
        const int sampleMiB = 4;
        try
        {
            string tempFolder = Path.GetTempPath();
            string? volume = Path.GetPathRoot(tempFolder);
            if (string.IsNullOrEmpty(volume) || new DriveInfo(volume).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            {
                return new(SystemTestStatus.Unavailable, "A pasta temporária da sessão não está em uma unidade local disponível.", []);
            }

            byte[] sample = RandomNumberGenerator.GetBytes(sampleMiB * BytesPerMiB);
            var readback = new byte[sample.Length];
            string testPath = Path.Combine(tempFolder, $"teste-disco-{Guid.NewGuid():N}.tmp");
            var items = new List<DiagnosticItem> { new("Unidade da pasta temporária", volume), new("Tamanho da amostra", $"{sampleMiB} MiB") };

            cancellationToken.ThrowIfCancellationRequested();
            await using var file = new FileStream(testPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 0, FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.WriteThrough);

            var timer = Stopwatch.StartNew();
            await file.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            timer.Stop();
            items.Add(new("Gravação da amostra", FormatDuration(timer.Elapsed)));

            file.Position = 0;
            timer.Restart();
            await file.ReadExactlyAsync(readback, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            items.Add(new("Leitura da amostra (pode usar cache)", FormatDuration(timer.Elapsed)));

            cancellationToken.ThrowIfCancellationRequested();
            return sample.AsSpan().SequenceEqual(readback)
                ? new(SystemTestStatus.Passed, "A amostra foi gravada, lida e conferida na pasta temporária.", items)
                : new(SystemTestStatus.Failed, "Os dados lidos diferem dos dados gravados.", items);
        }
        catch (EndOfStreamException)
        {
            return new(SystemTestStatus.Failed, "A leitura terminou antes de recuperar toda a amostra gravada.", []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return new(SystemTestStatus.Unavailable, "Não foi possível concluir o teste na pasta temporária.", []);
        }
    }

    private static string FormatDuration(TimeSpan elapsed) => string.Create(DisplayCulture, $"{elapsed.TotalMilliseconds:0.##} ms");

    private static string FormatRate(int sizeMiB, TimeSpan elapsed)
    {
        return elapsed > TimeSpan.Zero ? string.Create(DisplayCulture, $"{sizeMiB / elapsed.TotalSeconds:N1} MiB/s") : "Indisponível";
    }

    private sealed record Gateway(string Adapter, IPAddress Address);
}
