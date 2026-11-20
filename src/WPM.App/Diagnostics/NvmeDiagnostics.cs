using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WPM.App.Diagnostics;

internal sealed record StorageReliability(int? Temperature, int? Wear, ulong? Hours, ulong? Cycles,
    ulong? ReadErrors, ulong? WriteErrors, ulong? MediaErrors = null, byte? WarningFlags = null);

internal static class NvmeDiagnostics
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint StorageDeviceProtocolSpecificProperty = 50;
    private const uint ProtocolTypeNvme = 3;
    private const uint NvmeDataTypeLogPage = 2;
    private const uint NvmeHealthLogPage = 2;
    private const int ProtocolOffset = 8;
    private const int ProtocolSize = 40;
    private const int HealthLogSize = 512;
    private const uint FileShareReadWrite = 3;
    private const uint OpenExisting = 3;

    public static StorageReliability? Collect(uint diskNumber)
    {
        // Zero desired access permits querying metadata and never grants read/write access to sectors.
        using SafeFileHandle disk = CreateFile($@"\\.\PhysicalDrive{diskNumber}", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (disk.IsInvalid)
        {
            return null;
        }

        var request = new byte[ProtocolOffset + ProtocolSize + HealthLogSize];
        BinaryPrimitives.WriteUInt32LittleEndian(request, StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(ProtocolOffset), ProtocolTypeNvme);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(ProtocolOffset + 4), NvmeDataTypeLogPage);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(ProtocolOffset + 8), NvmeHealthLogPage);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(ProtocolOffset + 16), ProtocolSize);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(ProtocolOffset + 20), HealthLogSize);

        var result = new byte[request.Length];
        if (!DeviceIoControl(disk, IoctlStorageQueryProperty, request, request.Length, result, result.Length, out uint written, 0) ||
            written < ProtocolOffset + ProtocolSize + HealthLogSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(result) != ProtocolOffset + ProtocolSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)) != ProtocolOffset + ProtocolSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(ProtocolOffset)) != ProtocolTypeNvme ||
            BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(ProtocolOffset + 4)) != NvmeDataTypeLogPage)
        {
            return null;
        }

        uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(ProtocolOffset + 16));
        uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(ProtocolOffset + 20));
        if (dataOffset < ProtocolSize || dataLength < HealthLogSize || written > result.Length ||
            dataOffset > written - ProtocolOffset - HealthLogSize)
        {
            return null;
        }

        ReadOnlySpan<byte> log = result.AsSpan(ProtocolOffset + (int)dataOffset, HealthLogSize);
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        return new(kelvin is >= 150 and <= 500 ? kelvin - 273 : null, log[5],
            ReadHealthCounter(log[128..]), ReadHealthCounter(log[112..]), null, null, ReadHealthCounter(log[160..]), log[0]);
    }

    private static ulong? ReadHealthCounter(ReadOnlySpan<byte> counter)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(counter[8..]) == 0 ? BinaryPrimitives.ReadUInt64LittleEndian(counter) : null;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint desiredAccess, uint share, nint security, uint creation,
        uint flags, nint template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputLength, [Out] byte[] output,
        int outputLength, out uint written, nint overlapped);
}
