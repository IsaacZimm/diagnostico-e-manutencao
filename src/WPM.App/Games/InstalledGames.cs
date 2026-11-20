using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using WPM.App.Diagnostics;

namespace WPM.App.Games;

internal sealed record InstalledGame(string Name, string Store, string Folder);

internal sealed record GameHardware(string? Processor, IReadOnlyList<string> Graphics);

internal static class InstalledGames
{
    public static IReadOnlyList<InstalledGame> Scan()
    {
        return EnumerateSafely(Steam)
            .Concat(EnumerateSafely(Epic))
            .Concat(EnumerateSafely(Gog))
            .Concat(EnumerateSafely(Ubisoft))
            .DistinctBy(game => (game.Store, game.Folder.ToUpperInvariant()))
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static GameHardware ReadHardware()
    {
        string? processor = null;
        var graphics = new List<string>();
        WmiDiagnosticQuery.Read("SELECT Name FROM Win32_Processor", row => processor ??= row["Name"] as string);
        WmiDiagnosticQuery.Read("SELECT Name FROM Win32_VideoController", row =>
        {
            if (row["Name"] is string name && !string.IsNullOrWhiteSpace(name))
            {
                graphics.Add(name.Trim());
            }
        });
        return new(processor?.Trim(), graphics.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<InstalledGame> Steam()
    {
        using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        string? root = LocalFolder(user?.GetValue("SteamPath") as string)
            ?? LocalFolder(machine?.GetValue("InstallPath") as string)
            ?? LocalFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        if (root is null)
        {
            yield break;
        }

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        string? config = ReadManifest(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
        if (config is not null && Vdf.Parse(config).Child("libraryfolders") is { } folders)
        {
            var paths = folders.Children.Select(folder => folder.Value.Get("path"))
                .Concat(folders.Values.Where(folder => int.TryParse(folder.Key, out _)).Select(folder => folder.Value));
            foreach (string? entry in paths)
            {
                if (LocalFolder(entry) is { } path)
                {
                    libraries.Add(path);
                }
            }
        }

        foreach (string library in libraries)
        {
            string apps = Path.Combine(library, "steamapps");
            foreach (string file in Manifests(apps, "appmanifest_*.acf"))
            {
                string? manifest = ReadManifest(file);
                var app = manifest is null ? null : Vdf.Parse(manifest).Child("AppState");
                if (app?.Get("name") is not { Length: > 0 } name || app.Get("installdir") is not { Length: > 0 } entry
                    || entry is "." or ".." || entry.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || !long.TryParse(app.Get("StateFlags"), out long flags) || (flags & 4) == 0
                    || app.Get("appid") == "228980" || name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (LocalFolder(Path.Combine(apps, "common", entry)) is { } path)
                {
                    yield return new(name, "Steam", path);
                }
            }
        }
    }

    private static IEnumerable<InstalledGame> Epic()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        foreach (string manifest in Manifests(folder, "*.item"))
        {
            var game = ParseEpic(ReadManifest(manifest));
            if (game is not null)
            {
                yield return game;
            }
        }
    }

    private static InstalledGame? ParseEpic(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) ||
                (root.TryGetProperty("bIsApplication", out var application) && application.ValueKind == JsonValueKind.False))
            {
                return null;
            }

            return JsonString(root, "DisplayName") is { Length: > 0 } name && LocalFolder(JsonString(root, "InstallLocation")) is { } location
                ? new(name, "Epic Games", location)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? JsonString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static IEnumerable<InstalledGame> Gog() =>
        ReadRegistryLibrary(@"SOFTWARE\WOW6432Node\GOG.com\Games", "GOG", "path", "gameName");

    private static IEnumerable<InstalledGame> Ubisoft() =>
        ReadRegistryLibrary(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", "Ubisoft Connect", "InstallDir", null);

    private static IEnumerable<InstalledGame> ReadRegistryLibrary(string path, string store, string folderValue, string? nameValue)
    {
        using var root = Registry.LocalMachine.OpenSubKey(path);
        if (root is null)
        {
            yield break;
        }

        foreach (string key in root.GetSubKeyNames())
        {
            using var game = root.OpenSubKey(key);
            if (LocalFolder(game?.GetValue(folderValue) as string) is not { } folder)
            {
                continue;
            }

            string? name = nameValue is null ? null : game?.GetValue(nameValue) as string;
            yield return new(string.IsNullOrWhiteSpace(name) ? Path.GetFileName(folder) : name, store, folder);
        }
    }

    private static string? LocalFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length < 3
            || path[1] != ':' || !char.IsAsciiLetter(path[0]))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath)!;
            if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            {
                return null;
            }

            string current = root;
            foreach (string part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                {
                    return null;
                }
            }

            return Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Manifests(string folder, string pattern)
    {
        try
        {
            return LocalFolder(folder) is { } local ? Directory.GetFiles(local, pattern) : [];
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadManifest(string file)
    {
        try
        {
            if (LocalFolder(Path.GetDirectoryName(file)) is null
                || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int maximumBytes = 2 * 1024 * 1024;
            long length = stream.Length;
            if (length > maximumBytes)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            var characters = new char[(int)length + 1];
            int read = reader.ReadBlock(characters, 0, characters.Length);
            return read < characters.Length ? new string(characters, 0, read) : null;
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<InstalledGame> EnumerateSafely(Func<IEnumerable<InstalledGame>> scan)
    {
        var results = new List<InstalledGame>();
        try
        {
            results.AddRange(scan());
        }
        catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }

        return results;
    }
}
