using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;

namespace WPM.App.Tools;

internal sealed record LightingDevice(string Id, string Model, int LedCount);

internal sealed unsafe class CorsairLighting
{
    public const string DllName = "iCUESDK.x64_2019.dll";
    public const int Connected = 6;
    private const string SdkSha256 = "D72FD819B91FD1D3B0C3DB2EA17A47DCFE3B38E26269AA967ECFEE10D2E93884";
    private const uint SearchSystem32 = 0x00000800;
    private const int StringSize = 128;

    private nint library;
    private NativeConnect? connect;
    private NativeDisconnect? disconnect;
    private NativeGetDevices? getDevices;
    private NativeGetPositions? getPositions;
    private NativeSetColors? setColors;
    private readonly StateChanged callback;
    private int state = 1;
    private bool sessionOpen;

    public CorsairLighting() => callback = ReadState;

    public int State => Volatile.Read(ref state);

    public string? Connect(string path)
    {
        if (sessionOpen)
        {
            return null;
        }

        if (library == 0)
        {
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(SdkSha256, StringComparison.Ordinal))
            {
                return "Selecione a DLL x64 do pacote oficial do iCUE SDK 4.0.84.";
            }

            library = LoadLibraryEx(path, 0, SearchSystem32);
            if (library == 0)
            {
                return $"O Windows não conseguiu carregar a DLL do SDK (erro {Marshal.GetLastWin32Error()}).";
            }

            connect = GetExport<NativeConnect>("CorsairConnect");
            disconnect = GetExport<NativeDisconnect>("CorsairDisconnect");
            getDevices = GetExport<NativeGetDevices>("CorsairGetDevices");
            getPositions = GetExport<NativeGetPositions>("CorsairGetLedPositions");
            setColors = GetExport<NativeSetColors>("CorsairSetLedColors");
        }

        Volatile.Write(ref state, 2);
        int result = connect!(callback, 0);
        sessionOpen = result == 0;
        if (!sessionOpen)
        {
            Volatile.Write(ref state, 1);
        }

        return sessionOpen ? null : $"Não foi possível iniciar a sessão com o iCUE (código {result}).";
    }

    public void Disconnect()
    {
        if (sessionOpen)
        {
            disconnect!();
            sessionOpen = false;
        }

        Volatile.Write(ref state, 1);
    }

    public IReadOnlyList<LightingDevice> ReadDevices(out int error)
    {
        const int maximum = 64;
        var devices = new DeviceInfo[maximum];
        uint filter = uint.MaxValue;
        var result = new List<LightingDevice>();
        fixed (DeviceInfo* buffer = devices)
        {
            error = getDevices!(&filter, devices.Length, buffer, out int count);
            if (error != 0)
            {
                return result;
            }

            for (int index = 0; index < Math.Clamp(count, 0, devices.Length); index++)
            {
                var device = buffer + index;
                string id = ReadText(device->Id);
                string model = ReadText(device->Model);
                if (id.Length > 0 && device->LedCount > 0)
                {
                    result.Add(new(id, string.IsNullOrWhiteSpace(model) ? "Dispositivo Corsair" : model, device->LedCount));
                }
            }
        }

        return result;
    }

    public int ApplyColor(LightingDevice device, Color color)
    {
        const int maximum = 512;
        var positions = stackalloc LedPosition[maximum];
        int result = getPositions!(device.Id, maximum, positions, out int count);
        if (result != 0 || count <= 0 || count > maximum)
        {
            return result != 0 ? result : 6;
        }

        var colors = stackalloc LedColor[count];
        for (int index = 0; index < count; index++)
        {
            colors[index] = new LedColor { Id = positions[index].Id, Red = color.R, Green = color.G, Blue = color.B, Alpha = 255 };
        }

        return setColors!(device.Id, count, colors);
    }

    private T GetExport<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private void ReadState(nint context, nint data)
    {
        if (data != 0)
        {
            Volatile.Write(ref state, Marshal.ReadInt32(data));
        }
    }

    private static string ReadText(byte* buffer)
    {
        var text = new ReadOnlySpan<byte>(buffer, StringSize);
        int terminator = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(terminator < 0 ? text : text[..terminator]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public int Type;
        public fixed byte Id[StringSize];
        public fixed byte Serial[StringSize];
        public fixed byte Model[StringSize];
        public int LedCount;
        public int ChannelCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LedPosition
    {
        public uint Id;
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LedColor
    {
        public uint Id;
        public byte Red;
        public byte Green;
        public byte Blue;
        public byte Alpha;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StateChanged(nint context, nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeConnect(StateChanged callback, nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeDisconnect();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeGetDevices(uint* filter, int maximum, DeviceInfo* devices, out int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeGetPositions([MarshalAs(UnmanagedType.LPUTF8Str)] string id, int maximum, LedPosition* positions, out int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeSetColors([MarshalAs(UnmanagedType.LPUTF8Str)] string id, int size, LedColor* colors);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
}
