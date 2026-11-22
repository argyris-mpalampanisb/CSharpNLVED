using System.Reflection;
using System.Runtime.InteropServices;

namespace CSharpNLVED.Core;

/// <summary>Basic facts about the running app and platform.</summary>
public static class AppInfo
{
    public const string Name = "CSharpNLVED";

    /// <summary>Version like "0.1.0" (without the +commit suffix the SDK may append).</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Runtime identifier such as win-x64, linux-x64, osx-arm64.</summary>
    public static string RuntimeId => RuntimeInformation.RuntimeIdentifier;

    public static string OsDescription => RuntimeInformation.OSDescription;

    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(info))
            return "0.0.0";

        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
