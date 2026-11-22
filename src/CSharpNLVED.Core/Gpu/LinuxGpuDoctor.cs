using System.Text.RegularExpressions;

namespace CSharpNLVED.Core.Gpu;

/// <summary>
/// Read-only checks for the usual reasons GPU encoding breaks on Linux after an update:
/// kernel modules removed, NVIDIA module not loaded, driver/library version mismatch,
/// Secure Boot blocking an unsigned module, nouveau instead of NVIDIA's driver.
/// Never changes anything and never needs root.
/// </summary>
public static partial class LinuxGpuDoctor
{
    private static readonly string[] LibraryDirectories =
    [
        "/usr/lib/x86_64-linux-gnu",
        "/usr/lib/aarch64-linux-gnu",
        "/usr/lib/x86_64-linux-gnu/nvidia/current",
        "/usr/lib64",
        "/usr/lib64/nvidia",
        "/usr/lib",
        "/usr/lib/nvidia",
    ];

    public static IReadOnlyList<string> Diagnose(IReadOnlyList<GpuAdapter> adapters, IReadOnlyList<EncoderTestResult> results)
    {
        var hints = new List<string>();
        var anyGpuEncoderFailed = results.Any(r => !r.Works && r.Encoder.Api is GpuApi.Nvenc or GpuApi.Vaapi or GpuApi.Qsv);

        // 1. Kernel updated but not rebooted.
        var running = GpuEnvironment.TryRead("/proc/sys/kernel/osrelease")?.Trim();
        if (running is not null && Directory.Exists("/lib/modules"))
        {
            if (!Directory.Exists(Path.Combine("/lib/modules", running)))
            {
                hints.Add($"The kernel you're running ({running}) had its driver modules removed by a system update. " +
                          "GPU drivers can't load until you reboot.");
            }
            else if (anyGpuEncoderFailed && NewestInstalledKernel() is { } newest
                     && CompareKernelVersions(newest, running) > 0)
            {
                hints.Add($"A newer kernel ({newest}) is installed than the one running ({running}). " +
                          "If you updated recently, reboot to finish the update.");
            }
        }

        // 2. NVIDIA-specific checks, only if NVENC doesn't work.
        var nvidia = adapters.Where(a => a.Vendor == GpuVendor.Nvidia).ToList();
        var nvencWorks = results.Any(r => r.Works && r.Encoder.Api == GpuApi.Nvenc);
        if (nvidia.Count > 0 && !nvencWorks)
        {
            if (nvidia.All(a => a.KernelDriver == "nouveau"))
            {
                hints.Add("Your NVIDIA GPU is using the open-source \"nouveau\" driver, which can't use NVENC. " +
                          "Install NVIDIA's driver from your distro's repositories.");
            }
            else if (NvidiaKernelModuleVersion() is not { } module)
            {
                hints.Add("The NVIDIA kernel module isn't loaded. If you just updated, reboot. If it's still not loaded " +
                          "after a reboot, the module wasn't built for your kernel: check DKMS (Debian, Ubuntu, Arch) " +
                          "or akmods (Fedora).");
                if (SecureBootEnabled())
                    hints.Add("Secure Boot is on: the NVIDIA module must be signed with an enrolled key (MOK), " +
                              "or the kernel refuses to load it.");
            }
            else
            {
                var libraries = NvidiaEncodeLibraryVersions();
                if (libraries.Count == 0)
                {
                    hints.Add($"NVIDIA driver {module} is loaded, but its NVENC library (libnvidia-encode) wasn't found. " +
                              "On some distros it's a separate package.");
                }
                else if (!libraries.Contains(module))
                {
                    hints.Add($"Driver/library mismatch: the loaded NVIDIA kernel module is {module}, but the installed " +
                              $"libraries are {string.Join(", ", libraries)}. This happens after a driver update; " +
                              "reboot to fix it.");
                }
            }
        }

        // 3. AMD/Intel via VAAPI.
        var hasVaapiCapableGpu = adapters.Any(a => a.Vendor is GpuVendor.Amd or GpuVendor.Intel);
        var vaapiWorks = results.Any(r => r.Works && r.Encoder.Api == GpuApi.Vaapi);
        if (hasVaapiCapableGpu && !vaapiWorks)
        {
            hints.Add("VAAPI encoding didn't work on your AMD/Intel GPU. Make sure the VA-API driver is installed " +
                      "(e.g. mesa-va-drivers for AMD, intel-media-driver for Intel).");
        }

        return hints;
    }

    /// <summary>Version of the loaded NVIDIA kernel module, e.g. "580.95.05", or null if not loaded.</summary>
    public static string? NvidiaKernelModuleVersion()
    {
        var text = GpuEnvironment.TryRead("/proc/driver/nvidia/version");
        if (text is null)
            return null;

        var match = DriverVersionPattern().Match(text);
        return match.Success ? match.Value : null;
    }

    /// <summary>Versions of libnvidia-encode.so.&lt;version&gt; installed on the system.</summary>
    public static IReadOnlyList<string> NvidiaEncodeLibraryVersions()
    {
        var versions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dir in LibraryDirectories)
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;

                foreach (var file in Directory.EnumerateFiles(dir, "libnvidia-encode.so.*"))
                {
                    var match = EncodeLibraryPattern().Match(Path.GetFileName(file));
                    if (match.Success)
                        versions.Add(match.Groups["ver"].Value);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable directories.
            }
        }

        return versions.ToList();
    }

    private static bool SecureBootEnabled()
    {
        try
        {
            const string efivars = "/sys/firmware/efi/efivars";
            if (!Directory.Exists(efivars))
                return false;

            var file = Directory.EnumerateFiles(efivars, "SecureBoot-*").FirstOrDefault();
            if (file is null)
                return false;

            // 4 bytes of attributes, then the value: 1 = enabled.
            var bytes = File.ReadAllBytes(file);
            return bytes.Length >= 5 && bytes[4] == 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? NewestInstalledKernel()
    {
        try
        {
            return Directory.GetDirectories("/lib/modules")
                .Select(d => Path.GetFileName(d))
                .Where(n => n.Length > 0 && char.IsAsciiDigit(n[0]))
                .OrderByDescending(n => n, Comparer<string>.Create(CompareKernelVersions))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Compares "6.11.5-300.fc41" style names number by number.</summary>
    private static int CompareKernelVersions(string a, string b)
    {
        var na = NumberPattern().Matches(a).Select(m => long.Parse(m.Value)).ToArray();
        var nb = NumberPattern().Matches(b).Select(m => long.Parse(m.Value)).ToArray();
        for (var i = 0; i < Math.Min(na.Length, nb.Length); i++)
        {
            var c = na[i].CompareTo(nb[i]);
            if (c != 0)
                return c;
        }

        return na.Length.CompareTo(nb.Length);
    }

    [GeneratedRegex(@"\b\d{3}\.\d+(?:\.\d+)?\b")]
    private static partial Regex DriverVersionPattern();

    [GeneratedRegex(@"^libnvidia-encode\.so\.(?<ver>\d{3}\.\d+(?:\.\d+)?)$")]
    private static partial Regex EncodeLibraryPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();
}
