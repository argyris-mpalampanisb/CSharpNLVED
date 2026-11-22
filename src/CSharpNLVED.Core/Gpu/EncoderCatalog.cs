namespace CSharpNLVED.Core.Gpu;

/// <summary>Which hardware encoders are worth testing on this OS, in order of preference.</summary>
public static class EncoderCatalog
{
    public static IReadOnlyList<HardwareEncoder> ForCurrentPlatform()
    {
        GpuApi[] apis;
        if (OperatingSystem.IsWindows())
            apis = [GpuApi.Nvenc, GpuApi.Amf, GpuApi.Qsv];
        else if (OperatingSystem.IsLinux())
            apis = [GpuApi.Nvenc, GpuApi.Vaapi, GpuApi.Qsv];
        else if (OperatingSystem.IsMacOS())
            apis = [GpuApi.VideoToolbox];
        else
            apis = [];

        var list = new List<HardwareEncoder>();
        foreach (var api in apis)
        {
            foreach (var codec in new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 })
            {
                // Apple chips can decode AV1 but not encode it.
                if (api == GpuApi.VideoToolbox && codec == VideoCodec.Av1)
                    continue;

                list.Add(new HardwareEncoder(FfmpegName(api, codec), api, codec));
            }
        }

        return list;
    }

    private static string FfmpegName(GpuApi api, VideoCodec codec)
    {
        var prefix = codec switch
        {
            VideoCodec.H264 => "h264",
            VideoCodec.Hevc => "hevc",
            VideoCodec.Av1 => "av1",
            _ => throw new ArgumentOutOfRangeException(nameof(codec)),
        };

        var suffix = api switch
        {
            GpuApi.Nvenc => "nvenc",
            GpuApi.Amf => "amf",
            GpuApi.Qsv => "qsv",
            GpuApi.Vaapi => "vaapi",
            GpuApi.VideoToolbox => "videotoolbox",
            _ => throw new ArgumentOutOfRangeException(nameof(api)),
        };

        return $"{prefix}_{suffix}";
    }
}
