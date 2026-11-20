using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WPM.App.Maintenance;

internal sealed class TempCleanup
{
    public const int MinimumAgeDays = 7;

    private const int EntryLimit = 50_000;
    private const uint ReadDataOrListDirectory = 0x0001;
    private const uint ReadAttributes = 0x0080;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const FileAttributes ExcludedAttributes = FileAttributes.Directory | FileAttributes.ReparsePoint
        | FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.Offline
        | FileAttributes.Device;

    private readonly string? root;

    public TempCleanup() : this(GetProfileTempPath())
    {
    }

    internal TempCleanup(string? root)
    {
        this.root = string.IsNullOrWhiteSpace(root) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public TempCleanupPreview Scan()
    {
        if (root is null)
        {
            return new(this, [], 0, 0, "A pasta de temporários do perfil está indisponível.");
        }

        using var pinnedRoot = OpenDirectoryPath(root);
        if (pinnedRoot is null)
        {
            return new(this, [], 0, 0, "A pasta de temporários do perfil não pôde ser verificada.");
        }

        var files = new List<TempFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        int skipped = 0;
        int examined = 0;
        long bytes = 0;
        long cutoff = DateTime.UtcNow.AddDays(-MinimumAgeDays).ToFileTimeUtc();

        while (pending.TryPop(out string? directory))
        {
            using var pinnedDirectory = OpenDirectoryPath(directory);
            if (pinnedDirectory is null)
            {
                skipped++;
                continue;
            }

            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (examined++ == EntryLimit)
                    {
                        return new(this, files.AsReadOnly(), bytes, skipped,
                            "Prévia parcial. A busca foi limitada a 50.000 itens.");
                    }

                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0)
                            {
                                pending.Push(path);
                            }
                            else
                            {
                                skipped++;
                            }

                            continue;
                        }

                        if ((attributes & ExcludedAttributes) == 0 && HasTemporaryExtension(path))
                        {
                            using var file = Open(path, ReadAttributes | ReadDataOrListDirectory, FileShare.None);
                            if (TryReadFile(file, path, cutoff) is { } stamp)
                            {
                                files.Add(new(path, stamp));
                                bytes += stamp.Bytes;
                                continue;
                            }
                        }

                        skipped++;
                    }
                    catch (Exception error) when (IsFileError(error))
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception error) when (IsFileError(error))
            {
                skipped++;
            }
        }

        return new(this, files.AsReadOnly(), bytes, skipped, null);
    }

    public TempCleanupResult Clean(TempCleanupPreview preview)
    {
        if (root is null || !ReferenceEquals(preview.Owner, this))
        {
            return new(0, 0, preview.FileCount, "A prévia da limpeza é inválida. Faça uma nova análise.");
        }

        using var pinnedRoot = OpenDirectoryPath(root);
        if (pinnedRoot is null)
        {
            return new(0, 0, preview.FileCount, "A pasta de temporários ficou indisponível.");
        }

        int deleted = 0;
        long bytes = 0;
        long cutoff = DateTime.UtcNow.AddDays(-MinimumAgeDays).ToFileTimeUtc();

        foreach (var directory in preview.Files.GroupBy(file => Path.GetDirectoryName(file.Path)!))
        {
            using var pinnedDirectory = OpenDirectoryPath(directory.Key);
            if (pinnedDirectory is null)
            {
                continue;
            }

            foreach (var candidate in directory)
            {
                if (!candidate.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !HasTemporaryExtension(candidate.Path))
                {
                    continue;
                }

                using var file = Open(candidate.Path, ReadAttributes | ReadDataOrListDirectory | DeleteAccess, FileShare.None);
                if (TryReadFile(file, candidate.Path, cutoff) == candidate.Stamp
                    && SetFileInformationByHandle(file, FileInformationClass.Disposition, in DeleteFile, 1))
                {
                    deleted++;
                    bytes += candidate.Stamp.Bytes;
                }
            }
        }

        return new(deleted, bytes, preview.FileCount - deleted, null);
    }

    private static readonly byte DeleteFile = 1;

    private static string? GetProfileTempPath()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return localData.Length == 0 ? null : Path.Combine(localData, "Temp");
    }

    private static bool HasTemporaryExtension(string path)
    {
        return Path.GetExtension(path) is { } extension
            && (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".temp", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFileError(Exception error)
    {
        return error is IOException or UnauthorizedAccessException or SecurityException;
    }

    private static FileStamp? TryReadFile(SafeFileHandle handle, string path, long cutoff)
    {
        if (handle.IsInvalid || !GetBasicInfo(handle, FileInformationClass.Basic, out var basic, Marshal.SizeOf<BasicInfo>())
            || (basic.Attributes & ExcludedAttributes) != 0
            || basic.Created <= 0 || basic.Created >= cutoff || basic.LastWrite <= 0 || basic.LastWrite >= cutoff
            || !MatchesPath(handle, path)
            || !GetStandardInfo(handle, FileInformationClass.Standard, out var standard, Marshal.SizeOf<StandardInfo>())
            || standard.Links != 1 || standard.DeletePending != 0 || standard.Bytes < 0
            || !GetIdentity(handle, FileInformationClass.Identity, out var identity, Marshal.SizeOf<FileIdentity>()))
        {
            return null;
        }

        return new FileStamp(identity.Volume, identity.Low, identity.High, standard.Bytes,
            basic.Created, basic.LastWrite, basic.LastAccess, basic.LastChange);
    }

    private static DirectoryHandles? OpenDirectoryPath(string path)
    {
        string? drive = Path.GetPathRoot(path);
        if (drive is null || drive.Length != 3 || drive[1] != ':')
        {
            return null;
        }

        // No delete sharing pins each directory while examining paths below it.
        var pinned = new DirectoryHandles();
        string current = drive;
        foreach (var segment in path[drive.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var handle = Open(current, ReadAttributes | ReadDataOrListDirectory, FileShare.Read | FileShare.Write);
            pinned.Add(handle);
            if (handle.IsInvalid || !GetBasicInfo(handle, FileInformationClass.Basic, out var info, Marshal.SizeOf<BasicInfo>())
                || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory
                || !MatchesPath(handle, current))
            {
                pinned.Dispose();
                return null;
            }
        }

        return pinned;
    }

    private static SafeFileHandle Open(string path, uint access, FileShare sharing)
    {
        return CreateFile(@"\\?\" + path, access, sharing, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
    }

    private static bool MatchesPath(SafeFileHandle handle, string path)
    {
        string expected = @"\\?\" + path;
        var actual = new StringBuilder(expected.Length + 1);
        uint length = GetFinalPathNameByHandle(handle, actual, (uint)actual.Capacity, 0);
        return length == expected.Length && string.Equals(expected, actual.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DirectoryHandles : IDisposable
    {
        private readonly Stack<SafeFileHandle> handles = new();

        public void Add(SafeFileHandle handle) => handles.Push(handle);

        public void Dispose()
        {
            while (handles.TryPop(out var handle))
            {
                handle.Dispose();
            }
        }
    }

    private enum FileInformationClass
    {
        Basic = 0,
        Standard = 1,
        Disposition = 4,
        Identity = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long Created;
        public long LastAccess;
        public long LastWrite;
        public long LastChange;
        public FileAttributes Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long Allocation;
        public long Bytes;
        public uint Links;
        public byte DeletePending;
        public byte IsDirectory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong Volume;
        public ulong Low;
        public ulong High;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint securityAttributes,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasicInfo(SafeFileHandle file, FileInformationClass infoClass, out BasicInfo data, int size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(SafeFileHandle file, FileInformationClass infoClass, out StandardInfo data, int size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle file, FileInformationClass infoClass, out FileIdentity data, int size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, FileInformationClass infoClass, in byte data, int size);
}

internal sealed record TempCleanupPreview(
    TempCleanup Owner,
    IReadOnlyList<TempFile> Files,
    long Bytes,
    int Skipped,
    string? Issue)
{
    public int FileCount => Files.Count;
}

internal sealed record TempCleanupResult(int Deleted, long Bytes, int Skipped, string? Issue);

internal sealed record TempFile(string Path, FileStamp Stamp);

internal readonly record struct FileStamp(
    ulong Volume, ulong IdentityLow, ulong IdentityHigh, long Bytes,
    long Created, long LastWrite, long LastAccess, long LastChange);
