using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WPM.App.Optimization;

internal sealed record OptimizationResult(int Applied, int Skipped, IReadOnlyList<string> Messages);

internal sealed class SessionOptimizer
{
    private const uint QueryProcess = 0x1000;
    private const uint SetProcessInformation = 0x0200;
    private const uint QueryToken = 0x0008;
    private const uint CloseWindowMessage = 0x0010;
    private const int InvalidParameter = 87;
    private const ProcessPriorityClass ReducedPriority = ProcessPriorityClass.BelowNormal;

    private readonly Dictionary<(int Id, long StartTime), PriorityChange> priorityChanges = [];

    public int PendingPriorityChanges => priorityChanges.Count;

    public OptimizationResult Apply(OptimizationResource resource, IReadOnlyList<OptimizationAction> actions)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var messages = new List<string>();
        int applied = 0;

        foreach (var action in actions)
        {
            bool succeeded = TryApply(resource, action, identity.User, out string result);
            applied += succeeded ? 1 : 0;
            messages.Add($"{action.Process.AppName} (PID {action.Process.Id}): {result}");
        }

        return new OptimizationResult(applied, actions.Count - applied, messages);
    }

    public OptimizationResult Restore()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var messages = new List<string>();
        int restored = 0;

        foreach (var (key, change) in priorityChanges.ToArray())
        {
            using var handle = OpenProcess(QueryProcess | SetProcessInformation, false, change.Process.Id);
            TargetState state = CheckTarget(handle, change.Process, identity.User);
            bool keepPending;
            string result;

            if (state == TargetState.ReplacedOrClosed)
            {
                keepPending = false;
                result = "A sessão do aplicativo terminou.";
            }
            else if (state != TargetState.Verified)
            {
                keepPending = true;
                result = "O Windows não permitiu verificar o processo para restaurar a prioridade.";
            }
            else
            {
                ProcessPriorityClass current = GetPriorityClass(handle);
                if (current == ReducedPriority && SetPriorityClass(handle, change.Original))
                {
                    keepPending = false;
                    restored++;
                    result = "Prioridade original restaurada.";
                }
                else if (current != 0 && current != ReducedPriority)
                {
                    keepPending = false;
                    result = "A prioridade mudou desde o ajuste e foi preservada.";
                }
                else
                {
                    keepPending = true;
                    result = "A prioridade ainda não pôde ser restaurada. Você pode tentar novamente.";
                }
            }

            if (!keepPending)
            {
                priorityChanges.Remove(key);
            }

            messages.Add($"{change.Process.AppName} (PID {change.Process.Id}): {result}");
        }

        return new OptimizationResult(restored, messages.Count - restored, messages);
    }

    private bool TryApply(OptimizationResource resource, OptimizationAction action, SecurityIdentifier? owner, out string result)
    {
        OptimizationProcess expected = action.Process;
        if (!OptimizationPlanner.CanApply(resource, action.Kind, expected) || expected.Id == Environment.ProcessId)
        {
            result = "O aplicativo está fora do escopo da ação.";
            return false;
        }

        uint rights = action.Kind == OptimizationActionKind.LowerPriority ? QueryProcess | SetProcessInformation : QueryProcess;
        using var handle = OpenProcess(rights, false, expected.Id);
        TargetState state = CheckTarget(handle, expected, owner);
        if (state != TargetState.Verified)
        {
            result = state == TargetState.ReplacedOrClosed
                ? "O processo mudou ou foi encerrado. Faça uma nova análise."
                : "O Windows não permitiu confirmar a identidade e o acesso ao processo.";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(expected.Id);
            int foregroundId = GetWindowProcessId(GetForegroundWindow());
            if (foregroundId == 0 || foregroundId == expected.Id)
            {
                result = "O aplicativo pode estar em primeiro plano e foi preservado.";
                return false;
            }

            nint window = process.MainWindowHandle;
            if (GetWindowProcessId(window) != expected.Id)
            {
                result = "A janela do aplicativo ficou indisponível. Faça uma nova análise.";
                return false;
            }

            return action.Kind switch
            {
                OptimizationActionKind.LowerPriority => TryReducePriority(handle, expected, out result),
                OptimizationActionKind.RequestClose => TryRequestClose(window, out result),
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or Win32Exception)
        {
            result = "O aplicativo mudou de estado ou ficou inacessível. Faça uma nova análise.";
            return false;
        }
    }

    private bool TryReducePriority(SafeProcessHandle handle, OptimizationProcess expected, out string result)
    {
        ProcessPriorityClass original = GetPriorityClass(handle);
        if (original == 0)
        {
            result = "A prioridade atual ficou indisponível.";
            return false;
        }

        if (original != ProcessPriorityClass.Normal)
        {
            result = "O aplicativo já utiliza uma prioridade diferente de Normal.";
            return false;
        }

        if (!SetPriorityClass(handle, ReducedPriority))
        {
            result = "O Windows recusou o ajuste de prioridade.";
            return false;
        }

        priorityChanges[(expected.Id, expected.StartTimeUtcTicks)] = new PriorityChange(expected, original);
        result = "Prioridade ajustada para Abaixo do normal.";
        return true;
    }

    private static bool TryRequestClose(nint window, out string result)
    {
        bool requested = IsWindowEnabled(window) && PostMessage(window, CloseWindowMessage, 0, 0);
        result = requested
            ? "Pedido de fechamento enviado. Responda a eventuais avisos do aplicativo."
            : "O aplicativo não recebeu o pedido de fechamento.";
        return requested;
    }

    private static TargetState CheckTarget(SafeProcessHandle handle, OptimizationProcess expected, SecurityIdentifier? owner)
    {
        if (handle.IsInvalid)
        {
            return Marshal.GetLastPInvokeError() == InvalidParameter ? TargetState.ReplacedOrClosed : TargetState.Unavailable;
        }

        if (!GetProcessTimes(handle, out long creationTime, out _, out _, out _))
        {
            return TargetState.Unavailable;
        }

        if (DateTime.FromFileTimeUtc(creationTime).Ticks != expected.StartTimeUtcTicks)
        {
            return TargetState.ReplacedOrClosed;
        }

        var image = new StringBuilder(32768);
        int size = image.Capacity;
        if (!QueryFullProcessImageName(handle, 0, image, ref size))
        {
            return TargetState.Unavailable;
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(image.ToString()), expected.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return TargetState.ReplacedOrClosed;
        }

        return IsSameUser(handle, owner) ? TargetState.Verified : TargetState.Unavailable;
    }

    private static bool IsSameUser(SafeProcessHandle handle, SecurityIdentifier? owner)
    {
        bool opened = OpenProcessToken(handle, QueryToken, out var token);
        using (token)
        {
            if (!opened || owner is null)
            {
                return false;
            }

            try
            {
                using var processIdentity = new WindowsIdentity(token.DangerousGetHandle());
                return processIdentity.User is { } processOwner && owner.Equals(processOwner);
            }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or SecurityException)
            {
                return false;
            }
        }
    }

    private static int GetWindowProcessId(nint window)
    {
        return window != 0 && GetWindowThreadProcessId(window, out int processId) != 0 ? processId : 0;
    }

    private sealed record PriorityChange(OptimizationProcess Process, ProcessPriorityClass Original);

    private enum TargetState
    {
        Verified,
        ReplacedOrClosed,
        Unavailable
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ProcessPriorityClass GetPriorityClass(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(SafeProcessHandle process, ProcessPriorityClass priority);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
