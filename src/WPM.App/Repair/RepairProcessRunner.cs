using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WPM.App.Repair;

internal sealed record RepairUpdate(string? Text, double? Percent, bool IsError = false);

internal sealed record RepairResult(int? ExitCode, string? Failure = null, bool Cancelled = false);

internal static class RepairProcessRunner
{
    private const int ReadBufferSize = 4096;

    public static async Task<RepairResult> RunAsync(RepairOperation operation, Action<RepairUpdate> report)
    {
        string[] arguments = RepairCommands.GetArguments(operation);
        bool systemFiles = operation == RepairOperation.ScanSystemFiles;
        string systemDirectory = Environment.SystemDirectory;
        if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
        {
            systemDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(systemDirectory, systemFiles ? "sfc.exe" : "dism.exe"),
                WorkingDirectory = systemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            }
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var reportGate = new object();
        bool observerAvailable = true;

        void Publish(RepairUpdate update)
        {
            lock (reportGate)
            {
                if (!observerAvailable)
                {
                    return;
                }

                try
                {
                    report(update);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    // Losing the display connection must not interrupt an ongoing servicing operation.
                    observerAvailable = false;
                }
            }
        }

        try
        {
            if (!process.Start())
            {
                return new RepairResult(null, "O Windows não iniciou a ferramenta.");
            }

            process.StandardInput.Close();
            Task output = ReadOutputAsync(process.StandardOutput.BaseStream, systemFiles, isError: false, Publish);
            Task errors = ReadOutputAsync(process.StandardError.BaseStream, systemFiles, isError: true, Publish);
            await Task.WhenAll(output, errors, process.WaitForExitAsync()).ConfigureAwait(false);
            return new RepairResult(process.ExitCode);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new RepairResult(null, $"Não foi possível acompanhar a ferramenta do Windows. {exception.Message}");
        }
    }

    internal static async Task ReadOutputAsync(Stream stream, bool systemFiles, bool isError, Action<RepairUpdate> report)
    {
        byte[] buffer = new byte[ReadBufferSize];
        int size = 0;
        do
        {
            int read = await stream.ReadAsync(buffer.AsMemory(size)).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            size += read;
        } while (size < 4);

        if (size == 0)
        {
            return;
        }

        (Encoding encoding, int prefixLength) = DetectEncoding(buffer.AsSpan(0, size));
        Decoder decoder = encoding.GetDecoder();
        char[] characters = new char[encoding.GetMaxCharCount(buffer.Length)];
        var parser = new OutputParser(systemFiles, isError, report);

        void Decode(int offset, int count, bool flush = false)
        {
            int written = decoder.GetChars(buffer.AsSpan(offset, count), characters, flush);
            parser.Append(characters.AsSpan(0, written));
        }

        Decode(prefixLength, size - prefixLength);
        while ((size = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            Decode(0, size);
        }

        Decode(0, 0, flush: true);
        parser.Complete();
    }

    private static (Encoding Encoding, int PrefixLength) DetectEncoding(ReadOnlySpan<byte> prefix)
    {
        if (prefix.StartsWith(Encoding.UTF8.Preamble))
        {
            return (Encoding.UTF8, Encoding.UTF8.Preamble.Length);
        }

        if (prefix.StartsWith(Encoding.Unicode.Preamble))
        {
            return (Encoding.Unicode, Encoding.Unicode.Preamble.Length);
        }

        if (prefix.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return (Encoding.BigEndianUnicode, Encoding.BigEndianUnicode.Preamble.Length);
        }

        // SFC commonly writes UTF-16 without a BOM when redirected. Identify its ASCII punctuation
        // and other low code points by the zero high bytes before falling back to the Windows OEM page.
        int pairs = Math.Min(prefix.Length / 2, 64);
        int oddZeros = 0;
        int evenZeros = 0;
        for (int i = 0; i < pairs * 2; i += 2)
        {
            evenZeros += prefix[i] == 0 ? 1 : 0;
            oddZeros += prefix[i + 1] == 0 ? 1 : 0;
        }

        if (oddZeros > pairs / 3 && oddZeros > evenZeros * 2)
        {
            return (Encoding.Unicode, 0);
        }

        if (evenZeros > pairs / 3 && evenZeros > oddZeros * 2)
        {
            return (Encoding.BigEndianUnicode, 0);
        }

        if (LooksLikeUtf8(prefix))
        {
            return (Encoding.UTF8, 0);
        }

        int codePage = (int)GetOemCodePage();
        return (CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.UTF8, 0);
    }

    private static bool LooksLikeUtf8(ReadOnlySpan<byte> bytes)
    {
        bool hasMultibyte = false;
        while (!bytes.IsEmpty)
        {
            switch (Rune.DecodeFromUtf8(bytes, out _, out int consumed))
            {
                case System.Buffers.OperationStatus.Done:
                    hasMultibyte |= consumed > 1;
                    bytes = bytes[consumed..];
                    break;
                case System.Buffers.OperationStatus.NeedMoreData:
                    return hasMultibyte;
                default:
                    return false;
            }
        }

        return hasMultibyte;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetOEMCP")]
    private static extern uint GetOemCodePage();

    private sealed class OutputParser(bool systemFiles, bool isError, Action<RepairUpdate> report)
    {
        private static readonly Regex DismProgress = new(@"\[[= \t]*(?<value>[0-9]{1,3}(?:[.,][0-9]+)?)\s*[%％]$",
            RegexOptions.CultureInvariant);
        private static readonly Regex DismBars = new(@"\[[= \t]*[0-9]{1,3}(?:[.,][0-9]+)?[ \t]*[%％][= \t]*(?:\]|$)",
            RegexOptions.CultureInvariant);
        private static readonly Regex SfcProgress = new(@"(?<![0-9.,-])(?<value>[0-9]{1,3}(?:[.,][0-9]+)?)\s*[%％]$",
            RegexOptions.CultureInvariant);

        private readonly StringBuilder line = new();
        private double? lastPercent;
        private bool frameContainsDismProgress;

        public void Append(ReadOnlySpan<char> text)
        {
            foreach (char character in text)
            {
                if (character is '\r' or '\n' or '\b')
                {
                    Complete();
                    continue;
                }

                if (char.IsControl(character) && character != '\t')
                {
                    continue;
                }

                line.Append(character);
                if (!isError && character is '%' or '％')
                {
                    ReadPercent();
                }

                if (line.Length >= ReadBufferSize)
                {
                    Complete();
                }
            }
        }

        public void Complete()
        {
            string text = line.ToString().TrimEnd();
            line.Clear();
            if (frameContainsDismProgress)
            {
                // Keep a completion message printed beside a progress bar while omitting the bar itself.
                text = DismBars.Replace(text, string.Empty).TrimStart();
            }

            frameContainsDismProgress = false;
            if (!string.IsNullOrWhiteSpace(text))
            {
                report(new RepairUpdate(text, null, isError));
            }
        }

        private void ReadPercent()
        {
            Match match = (systemFiles ? SfcProgress : DismProgress).Match(line.ToString());
            if (!match.Success || !double.TryParse(match.Groups["value"].Value.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double percent) || percent is < 0 or > 100)
            {
                return;
            }

            frameContainsDismProgress = !systemFiles;
            if (percent != lastPercent)
            {
                lastPercent = percent;
                report(new RepairUpdate(null, percent));
            }
        }
    }
}
