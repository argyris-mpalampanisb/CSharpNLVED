using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using CSharpNLVED.Core.FFmpeg;

namespace CSharpNLVED.Core.Gpu;

/// <summary>Finds the graphics adapters and their driver versions, without admin/root rights.</summary>
public static class GpuEnvironment
{
    public static IReadOnlyList<GpuAdapter> DetectAdapters()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsAdapters();
            if (OperatingSystem.IsLinux())
                return LinuxAdapters();
            if (OperatingSystem.IsMacOS())
                return [new GpuAdapter(GpuVendor.Apple, "Apple GPU", Environment.OSVersion.VersionString, null)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Detection is best-effort; encoder tests are what really count.
        }

        return [];
    }

    /// <summary>
    /// A short hash that changes whenever something that affects hardware encoding changes:
    /// the FFmpeg build, the OS/kernel, or any GPU driver version.
    /// </summary>
    public static string Fingerprint(FFmpegInstallation ffmpeg, IReadOnlyList<GpuAdapter> adapters)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ffmpeg.FfmpegPath);
        sb.AppendLine(ffmpeg.VersionLine);
        sb.AppendLine(Environment.OSVersion.VersionString);

        if (OperatingSystem.IsLinux())
        {
            sb.AppendLine(TryRead("/proc/sys/kernel/osrelease"));
            sb.AppendLine(TryRead("/proc/driver/nvidia/version"));
        }

        foreach (var a in adapters)
            sb.Append(a.Vendor).Append('|').Append(a.Name).Append('|')
              .Append(a.DriverVersion).Append('|').AppendLine(a.KernelDriver);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16];
    }

    // ---------------------------------------------------------------- Windows

    [SupportedOSPlatform("windows")]
    private static List<GpuAdapter> WindowsAdapters()
    {
        // The "Display adapters" device class. Each numbered subkey is one adapter's driver.
        const string displayClass =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        var list = new List<GpuAdapter>();
        using var cls = Registry.LocalMachine.OpenSubKey(displayClass);
        if (cls is null)
            return list;

        foreach (var sub in cls.GetSubKeyNames())
        {
            if (sub.Length != 4 || !sub.All(char.IsAsciiDigit))
                continue;

            try
            {
                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string name || string.IsNullOrWhiteSpace(name))
                    continue;

                var provider = key.GetValue("ProviderName") as string ?? "";
                var version = key.GetValue("DriverVersion") as string;
                list.Add(new GpuAdapter(VendorFromText($"{name} {provider}"), name, version, null));
            }
            catch (SecurityException)
            {
                // Some subkeys are locked down; skip them.
            }
        }

        return list;
    }

    private static GpuVendor VendorFromText(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("nvidia")) return GpuVendor.Nvidia;
        if (t.Contains("radeon") || t.Contains("advanced micro") || t.Contains("amd ")) return GpuVendor.Amd;
        if (t.Contains("intel")) return GpuVendor.Intel;
        return GpuVendor.Other;
    }

    // ------------------------------------------------------------------ Linux

    private static List<GpuAdapter> LinuxAdapters()
    {
        const string drm = "/sys/class/drm";
        var list = new List<GpuAdapter>();
        if (!Directory.Exists(drm))
            return list;

        foreach (var card in Directory.GetDirectories(drm, "card*").Order())
        {
            var cardName = Path.GetFileName(card);
            if (cardName.Contains('-'))
                continue; // Connectors such as card0-HDMI-A-1, not GPUs.

            var device = Path.Combine(card, "device");
            var vendorId = TryRead(Path.Combine(device, "vendor"))?.Trim();
            var deviceId = TryRead(Path.Combine(device, "device"))?.Trim();
            var driver = UeventValue(Path.Combine(device, "uevent"), "DRIVER");

            var vendor = vendorId switch
            {
                "0x10de" => GpuVendor.Nvidia,
                "0x1002" => GpuVendor.Amd,
                "0x8086" => GpuVendor.Intel,
                _ => GpuVendor.Other,
            };

            var version = vendor == GpuVendor.Nvidia && driver == "nvidia"
                ? LinuxGpuDoctor.NvidiaKernelModuleVersion()
                : null;

            var ids = $"{vendorId?.Replace("0x", "")}:{deviceId?.Replace("0x", "")}";
            list.Add(new GpuAdapter(vendor, $"{vendor.DisplayName()} GPU [{ids}] ({cardName})", version, driver));
        }

        return list;
    }

    private static string? UeventValue(string path, string key)
    {
        var text = TryRead(path);
        if (text is null)
            return null;

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
                return line[(key.Length + 1)..].Trim();
        }

        return null;
    }

    internal static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
