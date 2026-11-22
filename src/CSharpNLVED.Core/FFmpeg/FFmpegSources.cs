using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CSharpNLVED.Core.Net;

namespace CSharpNLVED.Core.FFmpeg;

/// <summary>Somewhere we can get a ready-made FFmpeg build from.</summary>
public interface IFFmpegSource
{
    string Name { get; }
    Task<FFmpegRelease> GetLatestAsync(CancellationToken ct);
}

public static class FFmpegSources
{
    /// <summary>Picks the build source for the OS and CPU we're running on.</summary>
    public static IFFmpegSource? ForCurrentPlatform()
    {
        var arch = RuntimeInformation.OSArchitecture;

        if (OperatingSystem.IsWindows())
            return new GyanSource(); // x64 build; also runs under emulation on Windows on ARM.

        if (OperatingSystem.IsLinux())
            return arch switch
            {
                Architecture.X64 => new BtbNSource("linux64"),
                Architecture.Arm64 => new BtbNSource("linuxarm64"),
                _ => null,
            };

        if (OperatingSystem.IsMacOS())
            return arch switch
            {
                Architecture.Arm64 => new MartinRiedlSource("arm64"),
                Architecture.X64 => new MartinRiedlSource("amd64"),
                _ => null,
            };

        return null;
    }
}

/// <summary>
/// Windows: gyan.dev "full" release builds, taken from gyan's GitHub mirror
/// (GyanD/codexffmpeg), which offers the full build as a .zip. gyan.dev itself only
/// offers the full build as .7z, which .NET can't unpack without extra libraries.
/// </summary>
public sealed class GyanSource : IFFmpegSource
{
    public string Name => "gyan.dev";

    public async Task<FFmpegRelease> GetLatestAsync(CancellationToken ct)
    {
        var release = await GitHub.GetLatestReleaseAsync("GyanD", "codexffmpeg", ct).ConfigureAwait(false);

        var asset = release.Assets.FirstOrDefault(a =>
                        a.Name.EndsWith("-full_build.zip", StringComparison.OrdinalIgnoreCase))
                    ?? throw new FFmpegSourceException($"gyan.dev release {release.Tag} has no full_build.zip.");

        return new FFmpegRelease(
            Name,
            release.Tag,
            asset.Name,
            asset.UpdatedAt,
            [new FFmpegDownload(asset.DownloadUrl, asset.Sha256, ArchiveKind.Zip)]);
    }
}

/// <summary>
/// Linux: BtbN/FFmpeg-Builds. Its "latest" release is rebuilt daily and holds static
/// GPL builds per release branch, e.g. ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz.
/// They include NVENC, VAAPI and QSV support. We pick the newest release branch.
/// </summary>
public sealed partial class BtbNSource(string target) : IFFmpegSource
{
    public string Name => "BtbN FFmpeg-Builds";

    public async Task<FFmpegRelease> GetLatestAsync(CancellationToken ct)
    {
        var release = await GitHub.GetReleaseByTagAsync("BtbN", "FFmpeg-Builds", "latest", ct).ConfigureAwait(false);

        var best = release.Assets
            .Select(a => (Asset: a, Match: AssetPattern().Match(a.Name)))
            .Where(x => x.Match.Success && x.Match.Groups["target"].Value == target)
            .Select(x => (x.Asset, Version: Version.Parse(x.Match.Groups["ver"].Value)))
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();

        if (best.Asset is null)
            throw new FFmpegSourceException($"BtbN's latest release has no {target} release-branch build.");

        return new FFmpegRelease(
            Name,
            best.Version.ToString(),
            best.Asset.Name,
            best.Asset.UpdatedAt,
            [new FFmpegDownload(best.Asset.DownloadUrl, best.Asset.Sha256, ArchiveKind.TarXz)]);
    }

    [GeneratedRegex(@"^ffmpeg-n(?<ver>\d+\.\d+)-latest-(?<target>[a-z0-9]+)-gpl-\d+\.\d+\.tar\.xz$")]
    private static partial Regex AssetPattern();
}

/// <summary>
/// macOS: Martin Riedl's build server (static builds for Apple Silicon and Intel).
/// It has no JSON API, so we read the release history page and build the download
/// links from the newest entry: /download/macos/{arch}/{timestamp}_{version}/ffmpeg.zip
/// </summary>
public sealed partial class MartinRiedlSource(string arch) : IFFmpegSource
{
    private const string BaseUrl = "https://ffmpeg.martin-riedl.de";

    public string Name => "ffmpeg.martin-riedl.de";

    public async Task<FFmpegRelease> GetLatestAsync(CancellationToken ct)
    {
        var historyUrl = new Uri($"{BaseUrl}/info/history/macos/{arch}/release");
        var html = await Http.GetStringAsync(historyUrl, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        var best = DetailLink().Matches(html)
            .Where(m => m.Groups["arch"].Value == arch)
            .Select(m => (Id: m.Groups["id"].Value,
                          Timestamp: long.Parse(m.Groups["ts"].Value),
                          Version: m.Groups["ver"].Value))
            .OrderByDescending(x => x.Timestamp)
            .FirstOrDefault();

        if (best.Id is null)
            throw new FFmpegSourceException($"No macOS {arch} release build found on {Name}.");

        var folder = $"{BaseUrl}/download/macos/{arch}/{best.Id}";
        var downloads = new List<FFmpegDownload>();
        foreach (var tool in new[] { "ffmpeg", "ffprobe" })
        {
            var sha = await TryGetSha256Async(new Uri($"{folder}/{tool}.zip.sha256"), ct).ConfigureAwait(false);
            downloads.Add(new FFmpegDownload(new Uri($"{folder}/{tool}.zip"), sha, ArchiveKind.Zip));
        }

        return new FFmpegRelease(Name, best.Version, best.Id, DateTimeOffset.FromUnixTimeSeconds(best.Timestamp), downloads);
    }

    private static async Task<string?> TryGetSha256Async(Uri url, CancellationToken ct)
    {
        try
        {
            var text = await Http.GetStringAsync(url, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            var match = Sha256Pattern().Match(text);
            return match.Success ? match.Value : null;
        }
        catch (HttpRequestException)
        {
            return null; // No checksum published; download still goes over HTTPS.
        }
    }

    [GeneratedRegex(@"/info/detail/macos/(?<arch>[a-z0-9]+)/(?<id>(?<ts>\d+)_(?<ver>\d+(?:\.\d+)*))")]
    private static partial Regex DetailLink();

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Sha256Pattern();
}
