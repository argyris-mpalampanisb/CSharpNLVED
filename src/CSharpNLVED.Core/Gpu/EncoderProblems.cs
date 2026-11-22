namespace CSharpNLVED.Core.Gpu;

/// <summary>Turns FFmpeg's hardware-encoder error messages into plain language.</summary>
public static class EncoderProblems
{
    public static string Explain(HardwareEncoder encoder, string ffmpegError)
    {
        var e = ffmpegError;
        bool Has(string s) => e.Contains(s, StringComparison.OrdinalIgnoreCase);

        // AV1 encoding needs an RTX 40-series or newer; older cards fail with a vague error.
        if (encoder is { Api: GpuApi.Nvenc, Codec: VideoCodec.Av1 }
            && (Has("Invalid argument") || Has("No capable devices") || Has("not supported")))
            return "Your NVIDIA GPU can't encode AV1 (that needs an RTX 40-series or newer). H.264 and HEVC work.";

        switch (encoder.Api)
        {
            case GpuApi.Nvenc:
                if (Has("required nvenc API version"))
                    return "Your NVIDIA driver is too old for this FFmpeg build. Update the NVIDIA driver.";
                if (Has("Cannot load") || Has("nvcuda.dll") || Has("libcuda") || Has("nvEncodeAPI"))
                    return "NVIDIA driver libraries weren't found. No NVIDIA driver installed, or a broken install.";
                if (Has("No capable devices found") || Has("No NVENC capable devices"))
                    return "Your NVIDIA GPU can't encode this format.";
                if (Has("cuInit") || Has("CUDA_ERROR"))
                    return "CUDA failed to start. After a driver or kernel update, reboot first.";
                if (Has("OpenEncodeSessionEx failed"))
                    return "NVENC refused to start: GPU busy, too many encodes running, or a driver mismatch (reboot).";
                if (Has("Provided device doesn't support required NVENC features") || Has("not supported"))
                    return "Your NVIDIA GPU or driver doesn't support this format.";
                break;

            case GpuApi.Amf:
                if (Has("amfrt64") || Has("AMF failed to initialise") || Has("Failed to initialize AMF"))
                    return "AMD AMF isn't available (no AMD GPU, or the AMD driver isn't installed).";
                if (Has("not supported") || Has("Codec not supported"))
                    return "Your AMD GPU can't encode this format.";
                break;

            case GpuApi.Qsv:
                if (Has("MFX") || Has("Error initializing") || Has("unsupported"))
                    return "Intel Quick Sync isn't available for this format (no Intel GPU, or the driver lacks support).";
                break;

            case GpuApi.Vaapi:
                if (Has("vaInitialize failed") || Has("Failed to initialise VAAPI") || Has("No VA display found"))
                    return "No VA-API driver for this GPU. Install your GPU's VA-API driver.";
                if (Has("No usable encoding profile") || Has("No usable encoding entrypoint"))
                    return "Your GPU can't encode this format through VA-API.";
                break;

            case GpuApi.VideoToolbox:
                if (Has("cannot create compression session") || Has("Error"))
                    return "VideoToolbox couldn't create an encoder for this format on this Mac.";
                break;
        }

        if (Has("Unknown encoder"))
            return "Not included in this FFmpeg build.";

        return "FFmpeg could not start this encoder. See details.";
    }
}
