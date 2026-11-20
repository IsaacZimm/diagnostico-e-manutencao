using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WPM.App.Benchmarks;

internal static class LocalBenchmarks
{
    public const int SampleMiB = 128;
    public const int StressSeconds = 15;
    private const int MiB = 1024 * 1024;
    private const int Sector = 4096;
    private const int AssetSize = 64 * 1024;
    private const int AssetCount = 128;
    private const int SampleBytes = SampleMiB * MiB;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    internal sealed record MemoryResult(double LatencyNs, double ReadGiBs, double CopyGiBs);
    internal sealed record DiskResult(double SequentialRead, double SequentialWrite, double RandomRead, double RandomWrite);
    internal sealed record MapResult(double PackSeconds, double AssetsSeconds);
    internal sealed record StressResult(long Blocks, TimeSpan Elapsed);

    public static MemoryResult MeasureMemory(Action<string, double> update, CancellationToken token)
    {
        if (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < 1024L * MiB)
        {
            throw new InvalidOperationException("O teste de memória exige pelo menos 1 GiB disponível para o processo.");
        }

        update("Medindo acessos aleatórios à memória", 0.1);
        var buffer = new int[SampleBytes / sizeof(int)];
        int stride = 64 / sizeof(int);
        var order = Enumerable.Range(0, buffer.Length / stride).ToArray();
        var rng = new Random(4158);
        for (int index = order.Length - 1; index > 0; index--)
        {
            if ((index & 65535) == 0) token.ThrowIfCancellationRequested();
            int other = rng.Next(index + 1);
            (order[index], order[other]) = (order[other], order[index]);
        }

        for (int index = 0; index < order.Length; index++)
        {
            if ((index & 65535) == 0) token.ThrowIfCancellationRequested();
            buffer[order[index] * stride] = order[(index + 1) % order.Length] * stride;
        }

        int pointer = 0;
        for (int index = 0; index < 100_000; index++) pointer = buffer[pointer];
        const int accesses = 2_000_000;
        var timer = Stopwatch.StartNew();
        for (int index = 0; index < accesses; index++)
        {
            if ((index & 16383) == 0) token.ThrowIfCancellationRequested();
            pointer = buffer[pointer];
        }
        double latencyNs = timer.Elapsed.TotalNanoseconds / accesses;

        update("Medindo leitura e cópia de memória", 0.55);
        var copy = new int[buffer.Length];
        long checksum = pointer;
        for (int offset = 0; offset < copy.Length; offset += Sector / sizeof(int)) copy[offset] = 1;
        timer.Restart();
        for (int offset = 0; offset < buffer.Length; offset += MiB / sizeof(int))
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<int> chunk = buffer.AsSpan(offset, MiB / sizeof(int));
            foreach (int item in chunk) checksum += item;
        }
        double readGiBs = SampleBytes / (double)(1L << 30) / timer.Elapsed.TotalSeconds;

        timer.Restart();
        const int copies = 3;
        for (int attempt = 0; attempt < copies; attempt++)
        {
            for (int offset = 0; offset < buffer.Length; offset += MiB / sizeof(int))
            {
                token.ThrowIfCancellationRequested();
                Array.Copy(buffer, offset, copy, offset, MiB / sizeof(int));
            }
        }
        double copyGiBs = copies * SampleBytes / (double)(1L << 30) / timer.Elapsed.TotalSeconds;
        GC.KeepAlive(checksum);
        GC.KeepAlive(copy);
        return new(latencyNs, readGiBs, copyGiBs);
    }

    public static unsafe DiskResult MeasureDisk(string folder, Action<string, double> update, CancellationToken token)
    {
        using var file = CreateSampleFile(folder, token);
        using var buffer = new AlignedBuffer();

        update("Gravando a amostra sequencial", 0.1);
        double writeSeconds = WriteSample(file, buffer.Span, token);

        update("Lendo a amostra sequencial", 0.35);
        double readSeconds = ReadSample(file, buffer.Span, token);

        update("Lendo blocos aleatórios de 4 KiB", 0.6);
        double randomRead = MeasureRandom(file, buffer.Span[..Sector], false, token);
        update("Gravando blocos aleatórios de 4 KiB", 0.8);
        double randomWrite = MeasureRandom(file, buffer.Span[..Sector], true, token);
        return new(SampleMiB / readSeconds, SampleMiB / writeSeconds, randomRead, randomWrite);
    }

    public static unsafe MapResult MeasureMap(string folder, Action<string, double> update, CancellationToken token)
    {
        using var file = CreateSampleFile(folder, token);
        using var buffer = new AlignedBuffer();
        update("Preparando o pacote de teste", 0.15);
        WriteSample(file, buffer.Span, token);

        update("Carregando pacote de 128 MiB", 0.5);
        double packTime = ReadSample(file, buffer.Span, token);

        update("Carregando trechos de recursos do mapa", 0.8);
        var rng = new Random(201);
        Span<byte> asset = buffer.Span[..AssetSize];
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < AssetCount; i++)
        {
            token.ThrowIfCancellationRequested();
            ReadExact(file, asset, rng.Next(SampleBytes / AssetSize) * (long)AssetSize);
        }

        return new(packTime, timer.Elapsed.TotalSeconds);
    }

    public static StressResult StressProcessor(CancellationToken token)
    {
        const int bytesPerBlock = 256 * 1024;
        byte[] block = new byte[bytesPerBlock];
        byte[] expected = SHA256.HashData(block);
        int cores = Math.Clamp(Environment.ProcessorCount, 1, 64);
        long blocks = 0;
        var timer = Stopwatch.StartNew();

        Parallel.For(0, cores, new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = token }, _ =>
        {
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            long localCount = 0;
            while (timer.Elapsed.TotalSeconds < StressSeconds)
            {
                token.ThrowIfCancellationRequested();
                SHA256.HashData(block, digest);
                if (!digest.SequenceEqual(expected))
                {
                    throw new InvalidOperationException("O cálculo retornou um resultado inesperado.");
                }
                localCount++;
            }
            Interlocked.Add(ref blocks, localCount);
        });

        return new(blocks, timer.Elapsed);
    }

    private static SafeFileHandle CreateSampleFile(string folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? root = Path.GetPathRoot(Path.GetFullPath(folder));
        if (string.IsNullOrEmpty(root) || !Directory.Exists(folder))
        {
            throw new IOException("Selecione uma pasta local existente para os arquivos do teste.");
        }

        var drive = new DriveInfo(root);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
        {
            throw new IOException("Selecione uma unidade local fixa ou removível.");
        }
        if (drive.AvailableFreeSpace < (SampleMiB + 64L) * MiB)
        {
            throw new IOException($"A unidade precisa ter pelo menos {SampleMiB + 64} MiB livres.");
        }

        string path = Path.Combine(folder, $"amostra-benchmark-{Guid.NewGuid():N}.tmp");
        return File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            NoBuffering | FileOptions.WriteThrough | FileOptions.DeleteOnClose);
    }

    private static double WriteSample(SafeFileHandle file, Span<byte> buffer, CancellationToken token)
    {
        new Random(45).NextBytes(buffer);
        var timer = Stopwatch.StartNew();
        for (int offset = 0; offset < SampleBytes; offset += buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            RandomAccess.Write(file, buffer, offset);
        }
        return timer.Elapsed.TotalSeconds;
    }

    private static double ReadSample(SafeFileHandle file, Span<byte> buffer, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        for (int offset = 0; offset < SampleBytes; offset += buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            ReadExact(file, buffer, offset);
        }
        return timer.Elapsed.TotalSeconds;
    }

    private static double MeasureRandom(SafeFileHandle file, Span<byte> buffer, bool write, CancellationToken token)
    {
        var rng = new Random(write ? 831 : 839);
        var timer = Stopwatch.StartNew();
        int count = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            int offset = rng.Next(SampleBytes / Sector) * Sector;
            if (write)
            {
                RandomAccess.Write(file, buffer, offset);
            }
            else
            {
                ReadExact(file, buffer, offset);
            }
            count++;
        } while (timer.Elapsed < TimeSpan.FromSeconds(2));

        return count / timer.Elapsed.TotalSeconds;
    }

    private static void ReadExact(SafeFileHandle file, Span<byte> buffer, long offset)
    {
        if (RandomAccess.Read(file, buffer, offset) != buffer.Length)
        {
            throw new IOException("A amostra temporária retornou uma leitura incompleta.");
        }
    }

    private sealed unsafe class AlignedBuffer : IDisposable
    {
        private readonly void* memory = NativeMemory.AlignedAlloc(MiB, Sector);
        public Span<byte> Span => new(memory, MiB);
        public void Dispose() => NativeMemory.AlignedFree(memory);
    }
}
