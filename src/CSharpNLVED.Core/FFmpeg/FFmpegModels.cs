namespace CSharpNLVED.Core.FFmpeg;

public enum ArchiveKind
{
    Zip,
    TarXz,
}

/// <summary>One archive to download (some sources ship ffmpeg and ffprobe separately).</summary>
public sealed record FFmpegDownload(Uri Url, string? Sha256, ArchiveKind Kind);

/// <summary>The newest build a source offers.</summary>
/// <param name="Source">Human-readable source name, e.g. "gyan.dev".</param>
/// <param name="Version">Version as the source names it, e.g. "9.0.2".</param>
/// <param name="BuildId">Identifies the exact build (asset name or build folder).</param>
/// <param name="PublishedAt">When this build was published.</param>
public sealed record FFmpegRelease(
    string Source,
    string Version,
    string BuildId,
    DateTimeOffset PublishedAt,
    IReadOnlyList<FFmpegDownload> Downloads)
{
    // "System." needed: inside this record, plain "Version" means the string property above.
    public System.Version? NumericVersion => VersionText.TryParseNumeric(Version);
}

/// <summary>An FFmpeg that CSharpNLVED found and verified it can run.</summary>
/// <param name="VersionLine">What "ffmpeg -version" reports, e.g. "9.0.2-full_build-www.gyan.dev".</param>
/// <param name="IsManaged">True if it's CSharpNLVED's own copy in the data folder, false if it's a system install.</param>
public sealed record FFmpegInstallation(string FfmpegPath, string FfprobePath, string VersionLine, bool IsManaged);

/// <summary>Written next to the managed FFmpeg so we know what we installed.</summary>
public sealed record InstalledFFmpegInfo(
    string Source,
    string Version,
    string BuildId,
    DateTimeOffset PublishedAt,
    DateTimeOffset InstalledAt);

public sealed record FFmpegUpdateCheck(FFmpegRelease? Latest, bool UpdateAvailable, string? Error);

/// <param name="Stage">"Downloading", "Extracting", "Installing"…</param>
/// <param name="Fraction">0..1, or null when the stage has no measurable progress.</param>
public sealed record InstallProgress(string Stage, double? Fraction);

public sealed class FFmpegSourceException(string message) : Exception(message);
