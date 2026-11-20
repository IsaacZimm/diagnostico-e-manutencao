using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WPM.App.Repair;

internal static class RepairBroker
{
    public const string WorkerArgument = "--repair-worker";

    private const string PipePrefix = "WpmRepair-";
    private const string ServicingMutexName = @"Global\WindowsPerformanceManager.Repair";
    private const int ErrorCancelled = 1223;
    private const int ErrorInvalidData = 13;
    private const int ErrorAccessDenied = 5;
    private const int ErrorBrokenPipe = 109;
    private const PipeAccessRights WorkerPipeRights = PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes | PipeAccessRights.Synchronize;
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static async Task<RepairResult> RunAsync(RepairOperation operation, Action<RepairUpdate> report)
    {
        if (!Enum.IsDefined(operation))
        {
            return new(null, "Selecione uma operação de reparo da lista.");
        }

        string? appPath = Environment.ProcessPath;
        if (appPath is null || !Path.IsPathFullyQualified(appPath)
            || !Path.GetExtension(appPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(appPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new(null, "Abra o executável DiagnosticoManutencao.exe para usar o reparo do Windows.");
        }

        string pipeName = MakePipeName(Environment.ProcessId, Guid.NewGuid());
        try
        {
            using var pipe = CreateServer(pipeName);
            var startInfo = new ProcessStartInfo(appPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.SystemDirectory
            };
            startInfo.ArgumentList.Add(WorkerArgument);
            startInfo.ArgumentList.Add(operation.ToString());
            startInfo.ArgumentList.Add(pipeName);

            using var worker = await Task.Run(() => Process.Start(startInfo)).ConfigureAwait(false);
            if (worker is null)
            {
                return new(null, "O Windows não abriu o processo de reparo.");
            }

            await WaitForWorkerAsync(pipe, worker).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var message = JsonSerializer.Deserialize<WireMessage>(line);
                if (message?.Result is { } result)
                {
                    return result;
                }

                if (message?.Update is { } update)
                {
                    report(update);
                }
            }

            return new(null, "A comunicação terminou antes de o Windows informar o resultado.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == ErrorCancelled)
        {
            return new(null, "A solicitação de permissão administrativa foi cancelada.", Cancelled: true);
        }
        catch (OperationCanceledException)
        {
            return new(null, "O processo administrativo não se conectou ao painel a tempo.");
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException
            or InvalidOperationException or SecurityException or JsonException)
        {
            return new(null, $"Não foi possível acompanhar a operação de reparo. {error.Message}");
        }
    }

    public static Task<int> RunWorkerAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != WorkerArgument
            || !Enum.TryParse(args[1], ignoreCase: false, out RepairOperation operation)
            || !Enum.IsDefined(operation) || args[1] != operation.ToString()
            || !TryReadPipeName(args[2], out int parentId))
        {
            return Task.FromResult(ErrorInvalidData);
        }

        return Task.Run(() => RunWorker(operation, args[2], parentId));
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new UnauthorizedAccessException("A conta do painel ficou indisponível.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(Administrators, WorkerPipeRights, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 16 * 1024, 0, security);
    }

    private static async Task WaitForWorkerAsync(NamedPipeServerStream pipe, Process worker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var workerExited = worker.WaitForExitAsync(timeout.Token);
        try
        {
            while (true)
            {
                var connection = pipe.WaitForConnectionAsync(timeout.Token);
                if (await Task.WhenAny(connection, workerExited).ConfigureAwait(false) == workerExited
                    && !connection.IsCompleted && workerExited.IsCompletedSuccessfully)
                {
                    timeout.Cancel();
                    try
                    {
                        await connection.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new IOException($"O processo administrativo encerrou antes de se conectar (código {worker.ExitCode}).");
                    }
                }

                await connection.ConfigureAwait(false);
                if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientId) && clientId == worker.Id)
                {
                    return;
                }

                pipe.Disconnect();
            }
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
        }
    }

    private static int RunWorker(RepairOperation operation, string pipeName, int parentId)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return ErrorAccessDenied;
            }

            // Authentication is by the kernel-reported process ID. The panel receives no elevated token.
            using var pipe = new NamedPipeClientStream(".", pipeName, WorkerPipeRights, PipeOptions.None,
                TokenImpersonationLevel.Anonymous, HandleInheritability.None);
            pipe.Connect(25_000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverId) || serverId != parentId)
            {
                return ErrorAccessDenied;
            }

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            bool connected = true;
            bool Send(WireMessage message)
            {
                lock (writer)
                {
                    if (connected)
                    {
                        try
                        {
                            writer.WriteLine(JsonSerializer.Serialize(message));
                        }
                        catch (IOException)
                        {
                            // Keep the native repair running if the panel disconnects after launch.
                            connected = false;
                        }
                    }

                    return connected;
                }
            }

            if (!Send(new(Update: new RepairUpdate("Permissão administrativa concedida.", null))))
            {
                return ErrorBrokenPipe;
            }

            RepairResult result = RunExclusive(operation, update => Send(new(Update: update)));
            return Send(new(Result: result)) ? 0 : ErrorBrokenPipe;
        }
        catch (Exception error) when (error is IOException or TimeoutException or Win32Exception
            or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine(error);
            return ErrorBrokenPipe;
        }
    }

    private static RepairResult RunExclusive(RepairOperation operation, Action<RepairUpdate> report)
    {
        try
        {
            var security = new MutexSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new MutexAccessRule(Administrators, MutexRights.FullControl, AccessControlType.Allow));
            using var mutex = MutexAcl.Create(initiallyOwned: false, ServicingMutexName, out _, security);
            try
            {
                if (!mutex.WaitOne(0))
                {
                    return new(null, "Já existe uma operação de reparo em andamento.");
                }
            }
            catch (AbandonedMutexException)
            {
                // Windows passed ownership to this thread when the previous owner exited.
            }

            try
            {
                // A Windows mutex must be released by the thread that acquired it.
                return RepairProcessRunner.RunAsync(operation, report).GetAwaiter().GetResult();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            return new(null, $"Não foi possível executar o reparo. {error.Message}");
        }
    }

    private static string MakePipeName(int parentId, Guid nonce)
    {
        return $"{PipePrefix}{parentId.ToString(CultureInfo.InvariantCulture)}-{nonce:N}";
    }

    private static bool TryReadPipeName(string name, out int parentId)
    {
        parentId = 0;
        if (!name.StartsWith(PipePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> payload = name.AsSpan(PipePrefix.Length);
        int separator = payload.IndexOf('-');
        return separator > 0
            && int.TryParse(payload[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out parentId)
            && parentId > 0
            && Guid.TryParseExact(payload[(separator + 1)..], "N", out Guid nonce)
            && nonce != Guid.Empty && name == MakePipeName(parentId, nonce);
    }

    private sealed record WireMessage(RepairUpdate? Update = null, RepairResult? Result = null);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
