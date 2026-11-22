namespace CSharpNLVED.Core.Export;

public enum ExportQuality
{
    High,
    Balanced,
    Small,
}

/// <summary>Everything needed to run one export.</summary>
/// <param name="Arguments">FFmpeg command-line arguments (without the program name).</param>
/// <param name="OutputPath">Where the finished file goes.</param>
/// <param name="TempOutputPath">Where FFmpeg writes while working; renamed to OutputPath on success.</param>
/// <param name="Duration">Length of the finished video, for progress.</param>
/// <param name="InputFiles">Every source file used, so they can be checked before starting.</param>
/// <param name="EncoderName">FFmpeg encoder name, e.g. "hevc_nvenc".</param>
/// <param name="WorkFiles">
/// Helper files (text for drawtext, font copies) that must exist in FFmpeg's working folder.
/// The command refers to them by bare name, so no path ever needs filter-graph escaping.
/// </param>
public sealed record ExportPlan(
    IReadOnlyList<string> Arguments,
    string OutputPath,
    string TempOutputPath,
    TimeSpan Duration,
    IReadOnlyList<string> InputFiles,
    string EncoderName,
    IReadOnlyList<ExportWorkFile>? WorkFiles = null)
{
    public IReadOnlyList<ExportWorkFile> Files => WorkFiles ?? [];

    /// <summary>The command as you'd type it in a terminal (for logs).</summary>
    public string DisplayCommand =>
        "ffmpeg " + string.Join(" ", Arguments.Select(a => a.Contains(' ') || a.Contains(';') ? $"\"{a}\"" : a));
}

/// <summary>A file the export needs next to FFmpeg: either written from <paramref name="Text"/> (UTF-8) or copied from <paramref name="CopyFrom"/>.</summary>
/// <param name="Name">Plain file name, e.g. "text0.txt" or "font0.ttf".</param>
public sealed record ExportWorkFile(string Name, string? Text = null, string? CopyFrom = null);

/// <param name="Fraction">0..1</param>
/// <param name="Speed">How many times faster than real time, e.g. 4.2.</param>
public sealed record ExportProgress(double Fraction, TimeSpan Encoded, double? Speed, TimeSpan? Remaining);

/// <param name="LogPath">Full FFmpeg log for this export, if one was written.</param>
/// <param name="EncoderFailed">True if the GPU encoder itself seems to be what failed.</param>
public sealed class ExportException(string message, string? logPath = null, bool encoderFailed = false)
    : Exception(message)
{
    public string? LogPath { get; } = logPath;
    public bool EncoderFailed { get; } = encoderFailed;
}
