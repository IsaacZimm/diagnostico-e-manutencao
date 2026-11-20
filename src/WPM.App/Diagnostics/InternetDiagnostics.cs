using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WPM.App.Diagnostics;

internal static class InternetDiagnostics
{
    private const string Host = "speed.cloudflare.com";
    private const int DownloadBytes = 8 * 1024 * 1024;
    private const int UploadBytes = 2 * 1024 * 1024;
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        BaseAddress = new Uri($"https://{Host}"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<SystemTestResult> RunAsync(CancellationToken cancellationToken)
    {
        var items = new List<DiagnosticItem> { new("Destino da amostra", Host) };
        var dns = await TryMeasureAsync(MeasureDnsAsync, cancellationToken).ConfigureAwait(false);
        items.Add(new("Resolução DNS (pode usar cache)", dns is { } dnsMs ? Milliseconds(dnsMs) : "Indisponível"));
        await AddLatencyAsync(items, cancellationToken).ConfigureAwait(false);

        var download = await TryMeasureAsync(MeasureDownloadAsync, cancellationToken).ConfigureAwait(false);
        items.Add(new("Download · amostra de 8 MiB", download is { } down ? Megabits(down) : "Indisponível"));
        var upload = await TryMeasureAsync(MeasureUploadAsync, cancellationToken).ConfigureAwait(false);
        items.Add(new("Upload · amostra de 2 MiB", upload is { } up ? Megabits(up) : "Indisponível"));

        return download is not null && upload is not null
            ? new(SystemTestStatus.Passed, "Transferência de teste concluída. O resultado descreve a amostra e o caminho até a Cloudflare.", items)
            : new(SystemTestStatus.Unavailable, "Uma ou mais transferências da amostra ficaram indisponíveis. Confira as medições retornadas.", items);
    }

    private static async Task<double?> TryMeasureAsync(Func<CancellationToken, Task<double>> measure, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            double value = await measure(limit.Token).ConfigureAwait(false);
            return double.IsFinite(value) ? value : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or SocketException or IOException)
        {
            return null;
        }
    }

    private static async Task<double> MeasureDnsAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        await Dns.GetHostAddressesAsync(Host, cancellationToken).ConfigureAwait(false);
        return timer.Elapsed.TotalMilliseconds;
    }

    private static async Task AddLatencyAsync(List<DiagnosticItem> items, CancellationToken cancellationToken)
    {
        const int attempts = 8;
        var results = new List<long>(attempts);
        try
        {
            using var ping = new Ping();
            var target = IPAddress.Parse("1.1.1.1");
            for (int index = 0; index < attempts; index++)
            {
                var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(700),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    results.Add(reply.RoundtripTime);
                }
            }
        }
        catch (PingException)
        {
            items.Add(new("Latência ICMP até 1.1.1.1", "Indisponível"));
            return;
        }

        items.Add(new("Respostas ICMP de 1.1.1.1", $"{results.Count} de {attempts}"));
        items.Add(new("Latência média ICMP", results.Count == 0 ? "Sem resposta" : Milliseconds(results.Average())));
        if (results.Count > 1)
        {
            double jitter = results.Zip(results.Skip(1), (first, second) => Math.Abs(second - first)).Average();
            items.Add(new("Variação média de latência (jitter)", Milliseconds(jitter)));
        }
    }

    private static async Task<double> MeasureDownloadAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/__down?bytes={DownloadBytes}&cacheBust={Guid.NewGuid():N}");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var elapsed = Stopwatch.StartNew();
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[64 * 1024];
        int received = 0;
        while (received < DownloadBytes)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, DownloadBytes - received)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            received += read;
        }

        return received == DownloadBytes ? BitsPerSecond(received, elapsed.Elapsed) : double.NaN;
    }

    private static async Task<double> MeasureUploadAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__up")
        {
            Content = new ByteArrayContent(RandomNumberGenerator.GetBytes(UploadBytes))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var elapsed = Stopwatch.StartNew();
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return BitsPerSecond(UploadBytes, elapsed.Elapsed);
    }

    private static double BitsPerSecond(int size, TimeSpan elapsed) => size * 8d / elapsed.TotalSeconds;

    private static string Milliseconds(double ms) => string.Create(Portuguese, $"{ms:0.##} ms");

    private static string Megabits(double bps) => string.Create(Portuguese, $"{bps / 1_000_000:0.##} Mb/s");
}
