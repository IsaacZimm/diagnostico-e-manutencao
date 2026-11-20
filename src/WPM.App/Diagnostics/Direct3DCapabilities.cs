using System.Runtime.InteropServices;

namespace WPM.App.Diagnostics;

internal sealed record Direct3DCapabilities(string? FeatureLevel, string? RayTracing)
{
    private const uint MinimumD3D12FeatureLevel = 0xB000;
    private const uint FeatureLevels = 2;
    private const uint D3D12Options5 = 27;
    private const int CheckFeatureSupportSlot = 13;
    private static readonly Guid DeviceInterface = new("189819F1-1DB6-4B57-BE54-1821339B85F7");

    public static Direct3DCapabilities? Read(nint dxgiAdapter)
    {
        try
        {
            if (D3D12CreateDevice(dxgiAdapter, MinimumD3D12FeatureLevel, in DeviceInterface, out nint device) < 0 || device == 0)
            {
                return null;
            }

            try
            {
                nint table = Marshal.ReadIntPtr(device);
                nint method = Marshal.ReadIntPtr(table, CheckFeatureSupportSlot * IntPtr.Size);
                var checkLevels = Marshal.GetDelegateForFunctionPointer<CheckFeatureLevels>(method);
                var checkOptions = Marshal.GetDelegateForFunctionPointer<CheckOptions5>(method);
                int[] levels = [0xC200, 0xC100, 0xC000, 0xB100, 0xB000];
                var pin = GCHandle.Alloc(levels, GCHandleType.Pinned);
                try
                {
                    var query = new FeatureLevelQuery { Count = (uint)levels.Length, Requested = pin.AddrOfPinnedObject() };
                    string? featureLevel = checkLevels(device, FeatureLevels, ref query, (uint)Marshal.SizeOf<FeatureLevelQuery>()) >= 0
                        ? FormatFeatureLevel(query.Maximum)
                        : null;
                    var options = new Options5();
                    string? rayTracing = checkOptions(device, D3D12Options5, ref options, (uint)Marshal.SizeOf<Options5>()) >= 0
                        ? FormatRayTracing(options.RayTracing)
                        : null;
                    return new(featureLevel, rayTracing);
                }
                finally
                {
                    pin.Free();
                }
            }
            finally
            {
                Marshal.Release(device);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return null;
        }
    }

    private static string? FormatFeatureLevel(uint level) => level switch
    {
        0xC200 => "12_2",
        0xC100 => "12_1",
        0xC000 => "12_0",
        0xB100 => "11_1",
        0xB000 => "11_0",
        _ => null
    };

    private static string? FormatRayTracing(uint tier) => tier switch
    {
        0 => "Sem suporte",
        10 => "Nível 1.0",
        11 => "Nível 1.1",
        12 => "Nível 1.2",
        _ => null
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct FeatureLevelQuery
    {
        public uint Count;
        public nint Requested;
        public uint Maximum;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Options5
    {
        public int Tier3ResourceView;
        public uint RenderPass;
        public uint RayTracing;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckFeatureLevels(nint device, uint feature, ref FeatureLevelQuery query, uint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckOptions5(nint device, uint feature, ref Options5 query, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12CreateDevice(nint adapter, uint minimumFeatureLevel, in Guid interfaceId, out nint device);
}
