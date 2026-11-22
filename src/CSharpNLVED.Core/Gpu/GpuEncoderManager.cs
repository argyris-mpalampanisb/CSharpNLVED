using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpNLVED.Core.FFmpeg;

namespace CSharpNLVED.Core.Gpu;

/// <summary>
/// Finds out which GPU encoders actually work, by running a tiny test encode with each.
///
/// Results are cached in data/gpu-encoders.json together with a fingerprint of the
/// FFmpeg build, kernel and GPU drivers. Unlike Shotcut/Kdenlive, the cache is thrown
/// away automatically whenever any of those change, and a result with no working
/// encoder is never trusted: it's re-tested on every launch.
/// </summary>
public sealed partial class GpuEncoderManager
{
    /// <summary>Generous: the first CUDA start after a driver update can take several seconds.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    private static string ReportPath => Path.Combine(AppPaths.DataDirectory, "gpu-encoders.json");

    public EncoderReport? Report { get; private set; }

    public async Task<EncoderReport> GetReportAsync(
        FFmpegInstallation ffmpeg,
        bool forceRecheck,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var adapters = await Task.Run(() => GpuEnvironment.DetectAdapters(), ct).ConfigureAwait(false);
            var fingerprint = GpuEnvironment.Fingerprint(ffmpeg, adapters);

            if (!forceRecheck && Load() is { } cached && cached.Fingerprint == fingerprint && cached.HasWorkingEncoder)
                return Report = cached;

            var report = await DetectAsync(ffmpeg, adapters, fingerprint, progress, ct).ConfigureAwait(false);
            Save(report);
            return Report = report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forget the cached results, e.g. after a GPU export failed. Next check re-tests.</summary>
    public void Invalidate()
    {
        Report = null;
        try
        {
            File.Delete(ReportPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<EncoderReport> DetectAsync(
        FFmpegInstallation ffmpeg,
        IReadOnlyList<GpuAdapter> adapters,
        string fingerprint,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var compiled = await ListCompiledEncodersAsync(ffmpeg, ct).ConfigureAwait(false);

        var vaapiDevices = OperatingSystem.IsLinux() && Directory.Exists("/dev/dri")
            ? Directory.GetFiles("/dev/dri", "renderD*").Order().ToArray()
            : Array.Empty<string>();

        var results = new List<EncoderTestResult>();
        foreach (var encoder in EncoderCatalog.ForCurrentPlatform())
        {
            ct.ThrowIfCancellationRequested();

            if (!compiled.Contains(encoder.FfmpegName))
            {
                results.Add(new EncoderTestResult(encoder, false, "Not included in this FFmpeg build.", null, null));
                continue;
            }

            progress?.Report($"testing {encoder.FfmpegName}…");

            if (encoder.Api == GpuApi.Vaapi)
            {
                if (vaapiDevices.Length == 0)
                {
                    results.Add(new EncoderTestResult(encoder, false, "No GPU render device found (/dev/dri/renderD*).", null, null));
                    continue;
                }

                // Machines with two GPUs have two render nodes; use the first one that works.
                EncoderTestResult? last = null;
                foreach (var device in vaapiDevices)
                {
                    last = await TestAsync(ffmpeg, encoder, device, ct).ConfigureAwait(false);
                    if (last.Works)
                        break;
                }

                results.Add(last!);
            }
            else
            {
                results.Add(await TestAsync(ffmpeg, encoder, null, ct).ConfigureAwait(false));
            }
        }

        var hints = new List<string>();
        if (OperatingSystem.IsLinux())
            hints.AddRange(LinuxGpuDoctor.Diagnose(adapters, results));
        if (!results.Any(r => r.Works))
            hints.Add("No working GPU encoder was found, so exporting is disabled until one works.");

        return new EncoderReport(fingerprint, DateTimeOffset.UtcNow, adapters, results, hints);
    }

    /// <summary>Encodes 15 black 720p frames to nowhere. Takes about a second when the GPU works.</summary>
    private static async Task<EncoderTestResult> TestAsync(
        FFmpegInstallation ffmpeg, HardwareEncoder encoder, string? vaapiDevice, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (vaapiDevice is not null)
            args.AddRange(["-vaapi_device", vaapiDevice]);

        args.AddRange(["-f", "lavfi", "-i", "color=c=black:s=1280x720:r=30", "-frames:v", "15"]);

        if (vaapiDevice is not null)
            args.AddRange(["-vf", "format=nv12,hwupload"]); // VAAPI needs frames uploaded to the GPU.
        else
            args.AddRange(["-pix_fmt", "nv12"]);

        args.AddRange(["-c:v", encoder.FfmpegName, "-f", "null", "-"]);

        try
        {
            var result = await ProcessRunner.RunAsync(ffmpeg.FfmpegPath, args, TestTimeout, ct).ConfigureAwait(false);
            if (result.ExitCode == 0)
                return new EncoderTestResult(encoder, true, null, null, vaapiDevice);

            var details = Tail(result.StdErr);
            return new EncoderTestResult(encoder, false, EncoderProblems.Explain(encoder, details), details, vaapiDevice);
        }
        catch (TimeoutException)
        {
            return new EncoderTestResult(encoder, false,
                $"The GPU didn't respond within {TestTimeout.TotalSeconds:0} seconds.", null, vaapiDevice);
        }
    }

    private static async Task<HashSet<string>> ListCompiledEncodersAsync(FFmpegInstallation ffmpeg, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(ffmpeg.FfmpegPath, ["-hide_banner", "-encoders"],
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        // Lines look like: " V....D hevc_nvenc   NVIDIA NVENC hevc encoder (codec hevc)"
        return EncoderLine().Matches(result.StdOut)
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Last few meaningful lines of FFmpeg's error output. FFmpeg ends every failure with the
    /// same generic lines ("Task finished with error code", "Nothing was written…"), which
    /// would hide the real reason, so those are skipped.
    /// </summary>
    private static string Tail(string stderr)
    {
        string[] generic = ["Task finished with error code", "Terminating thread with return code",
                            "Nothing was written into output file", "Conversion failed", "Error while filtering"];
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var meaningful = lines.Where(l => !generic.Any(g => l.Contains(g, StringComparison.OrdinalIgnoreCase))).ToList();
        var text = string.Join(" / ", (meaningful.Count > 0 ? meaningful : lines.ToList()).TakeLast(3));
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    private static EncoderReport? Load()
    {
        try
        {
            return File.Exists(ReportPath)
                ? JsonSerializer.Deserialize<EncoderReport>(File.ReadAllText(ReportPath), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static void Save(EncoderReport report)
    {
        try
        {
            File.WriteAllText(ReportPath, JsonSerializer.Serialize(report, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: we'll just test again next launch.
        }
    }

    [GeneratedRegex(@"^\s*V\S{5}\s+(?<name>\S+)", RegexOptions.Multiline)]
    private static partial Regex EncoderLine();
}
