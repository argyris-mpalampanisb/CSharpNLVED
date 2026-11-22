using System.Globalization;
using System.Text.Json;
using CSharpNLVED.Core.FFmpeg;
using CSharpNLVED.Core.Projects;

namespace CSharpNLVED.Core.Media;

/// <summary>Reads a media file's length, streams, size and frame rate with ffprobe.</summary>
public static class MediaProbe
{
    public static async Task<MediaItem> ProbeAsync(FFmpegInstallation ffmpeg, string path, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileName(fullPath);

        var result = await ProcessRunner.RunAsync(ffmpeg.FfprobePath,
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", fullPath],
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var reason = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "unknown error";
            throw new InvalidDataException($"{fileName} couldn't be read: {reason}");
        }

        using var doc = JsonDocument.Parse(result.StdOut);
        var root = doc.RootElement;
        var item = new MediaItem { Path = fullPath };
        var longestStream = 0.0;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = GetString(stream, "codec_type");
                longestStream = Math.Max(longestStream, ParseSeconds(GetString(stream, "duration")));

                if (type == "audio")
                {
                    item.HasAudio = true;
                }
                else if (type == "video" && !item.HasVideo && !IsCoverArt(stream))
                {
                    item.HasVideo = true;
                    var width = GetInt(stream, "width");
                    var height = GetInt(stream, "height");

                    // Phone videos are often stored sideways with a "rotate 90°" flag.
                    // FFmpeg rotates them when exporting, so report the size as shown.
                    if (Math.Abs(Rotation(stream)) % 180 == 90)
                        (width, height) = (height, width);

                    item.Width = width;
                    item.Height = height;
                    item.FrameRate = ParseRational(GetString(stream, "avg_frame_rate"))
                                     ?? ParseRational(GetString(stream, "r_frame_rate"));
                }
            }
        }

        var formatDuration = root.TryGetProperty("format", out var format)
            ? ParseSeconds(GetString(format, "duration"))
            : 0;
        item.Duration = TimeSpan.FromSeconds(formatDuration > 0 ? formatDuration : longestStream);

        if (!item.HasVideo && !item.HasAudio)
            throw new InvalidDataException($"{fileName} has no video or audio.");
        if (item.Duration <= TimeSpan.Zero)
            throw new InvalidDataException($"{fileName} has no length (still images aren't supported yet).");

        return item;
    }

    private static bool IsCoverArt(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var d)
        && d.TryGetProperty("attached_pic", out var pic)
        && pic.ValueKind == JsonValueKind.Number
        && pic.GetInt32() == 1;

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (entry.TryGetProperty("rotation", out var r) && r.TryGetInt32(out var degrees))
                    return degrees;
            }
        }

        if (stream.TryGetProperty("tags", out var tags)
            && int.TryParse(GetString(tags, "rotate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tagDegrees))
            return tagDegrees;

        return 0;
    }

    private static Rational? ParseRational(string? text)
    {
        if (text is null)
            return null;

        var parts = text.Split('/');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den)
            && num > 0 && den > 0)
            return new Rational(num, den);

        return null;
    }

    private static double ParseSeconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : 0;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}
