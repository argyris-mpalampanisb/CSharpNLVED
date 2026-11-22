namespace CSharpNLVED.Core;

/// <summary>
/// Where CSharpNLVED keeps its files.
///
/// Portable by default: everything goes into a "data" folder next to the executable,
/// so the whole app can live on a USB stick. If that folder is not writable
/// (e.g. installed under Program Files, a read-only AppImage mount, or a macOS .app
/// bundle), we fall back to the per-user application data folder.
/// </summary>
public static class AppPaths
{
    /// <summary>Folder containing the CSharpNLVED executable.</summary>
    public static string AppDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>True when data is stored next to the executable.</summary>
    public static bool IsPortable { get; private set; }

    /// <summary>Root folder for settings, tools, logs and caches.</summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string ToolsDirectory => Ensure(Path.Combine(DataDirectory, "tools"));
    public static string FFmpegDirectory => Ensure(Path.Combine(ToolsDirectory, "ffmpeg"));
    public static string LogsDirectory => Ensure(Path.Combine(DataDirectory, "logs"));
    public static string CacheDirectory => Ensure(Path.Combine(DataDirectory, "cache"));
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    private static string ResolveDataDirectory()
    {
        var portable = Path.Combine(AppDirectory, "data");
        if (CanWriteTo(portable))
        {
            IsPortable = true;
            return portable;
        }

        var userRoot = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        var fallback = Path.Combine(userRoot, AppInfo.Name);
        Directory.CreateDirectory(fallback);
        IsPortable = false;
        return fallback;
    }

    private static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
