using System.Text.Json.Serialization;

namespace CSharpNLVED.Core.Gpu;

/// <summary>The hardware encoding APIs FFmpeg can use. CSharpNLVED is GPU-only: no CPU encoders.</summary>
public enum GpuApi
{
    Nvenc,
    Amf,
    Qsv,
    Vaapi,
    VideoToolbox,
}

public enum VideoCodec
{
    H264,
    Hevc,
    Av1,
}

public enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
    Apple,
    Other,
}

public static class GpuNames
{
    public static string DisplayName(this GpuApi api) => api switch
    {
        GpuApi.Nvenc => "NVIDIA NVENC",
        GpuApi.Amf => "AMD AMF",
        GpuApi.Qsv => "Intel Quick Sync",
        GpuApi.Vaapi => "VAAPI",
        GpuApi.VideoToolbox => "Apple VideoToolbox",
        _ => api.ToString(),
    };

    public static string DisplayName(this VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Hevc => "HEVC",
        VideoCodec.Av1 => "AV1",
        _ => codec.ToString(),
    };

    public static string DisplayName(this GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        GpuVendor.Apple => "Apple",
        _ => "Other",
    };
}

/// <param name="FfmpegName">FFmpeg's encoder name, e.g. "hevc_nvenc".</param>
public sealed record HardwareEncoder(string FfmpegName, GpuApi Api, VideoCodec Codec)
{
    [JsonIgnore]
    public string DisplayName => $"{Api.DisplayName()} {Codec.DisplayName()}";
}

/// <param name="KernelDriver">Linux only: the kernel driver in use (nvidia, nouveau, amdgpu, i915, xe…).</param>
public sealed record GpuAdapter(GpuVendor Vendor, string Name, string? DriverVersion, string? KernelDriver);

/// <param name="Problem">Plain-language reason it failed (null when it works).</param>
/// <param name="Details">FFmpeg's own error output, for the curious.</param>
/// <param name="Device">VAAPI only: the render node that worked or was last tried.</param>
public sealed record EncoderTestResult(HardwareEncoder Encoder, bool Works, string? Problem, string? Details, string? Device);

/// <param name="Fingerprint">
/// Hash of FFmpeg version + GPU drivers + kernel. When it changes, the report is stale
/// and the encoders are tested again automatically.
/// </param>
/// <param name="Hints">Suggestions for fixing what didn't work.</param>
public sealed record EncoderReport(
    string Fingerprint,
    DateTimeOffset TestedAt,
    IReadOnlyList<GpuAdapter> Adapters,
    IReadOnlyList<EncoderTestResult> Results,
    IReadOnlyList<string> Hints)
{
    [JsonIgnore]
    public bool HasWorkingEncoder => Results.Any(r => r.Works);

    /// <summary>
    /// Best working encoder for a codec. Results are in preference order
    /// (NVENC, then AMF/VAAPI, then Quick Sync), so the first working one wins.
    /// </summary>
    public EncoderTestResult? Preferred(VideoCodec codec) =>
        Results.FirstOrDefault(r => r.Works && r.Encoder.Codec == codec);
}
