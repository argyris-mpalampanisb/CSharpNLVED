using System.Globalization;
using CSharpNLVED.Core.Gpu;
using CSharpNLVED.Core.Projects;
using CSharpNLVED.Core.Text;

namespace CSharpNLVED.Core.Export;

/// <summary>
/// Turns a project into one FFmpeg command.
///
/// Every clip becomes its own input, opened at its In point for its length (-ss/-t),
/// so FFmpeg only decodes what's used. Then one filter graph:
///
///   each video track: clips scaled/padded to the project size, gaps filled with black
///     (bottom track) or transparency (tracks above), fades applied → joined end to end
///   each track's text clips drawn over it with drawtext (fades = alpha ramps)
///   video tracks stacked bottom to top with overlay → nv12 (→ GPU upload for VAAPI) → GPU encoder
///
///   each audio track: clips + silent gaps, fades → joined → track volume
///   all audio tracks mixed together (volumes kept as set) → AAC
///
/// Every track is padded to the full project length so everything lines up.
/// </summary>
public static class ExportPlanner
{
    /// <param name="findFont">Font family → font file (default: <see cref="FontLocator.Find"/>; tests pass their own).</param>
    public static ExportPlan Build(Project project, EncoderTestResult encoder, ExportQuality quality, string outputPath,
        Func<string, string?>? findFont = null)
    {
        if (!encoder.Works)
            throw new ExportException($"{encoder.Encoder.DisplayName} isn't working on this computer.");

        var duration = project.Duration;
        if (duration <= TimeSpan.Zero)
            throw new ExportException("Add at least one clip before exporting.");

        // Transitions (overlapping clips) are exported in Phase 4 step 2; until then, say so plainly.
        if (project.AllTracks.Any(t => t.Clips.OrderBy(c => c.Start).Zip(t.Clips.OrderBy(c => c.Start).Skip(1)).Any(p => p.Second.Start < p.First.End)))
            throw new ExportException("This project has transitions, and CSharpNLVED can't export those yet (coming in the next update). Undo them (Ctrl+Z) to export now.");

        var s = project.Settings;
        var width = Math.Max(2, s.Width & ~1);   // encoders need even sizes
        var height = Math.Max(2, s.Height & ~1);
        var size = $"{width}x{height}";
        var fps = s.FrameRate.Numerator > 0 && s.FrameRate.Denominator > 0 ? s.FrameRate.ToString() : "30";
        var sampleRate = s.SampleRate > 0 ? s.SampleRate : 48000;
        var layout = s.AudioChannels == 1 ? "mono" : "stereo";
        var audioFormat = $"aresample={sampleRate},aformat=sample_fmts=fltp:channel_layouts={layout}";

        var isVaapi = encoder.Encoder.Api == GpuApi.Vaapi;
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-y" };
        if (isVaapi && encoder.Device is not null)
            args.AddRange(["-vaapi_device", encoder.Device]);

        var filters = new List<string>();
        var inputFiles = new List<string>();
        var labelCounter = 0;
        string NewLabel(string prefix) => $"{prefix}{labelCounter++}";

        // Opens the clip's part of its file as a new input. Video inputs skip the sound
        // (-an) and audio inputs skip the picture (-vn), so nothing is decoded twice for nothing.
        string AddInput(Clip clip, bool video)
        {
            var media = project.FindMedia(clip.MediaId)
                        ?? throw new ExportException("A clip refers to a file that isn't in the project.");
            args.AddRange(["-ss", Seconds(clip.In), "-t", Seconds(clip.Length), video ? "-an" : "-vn", "-i", media.Path]);
            inputFiles.Add(media.Path);
            return (inputFiles.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        string Join(List<string> labels, bool video)
        {
            var output = NewLabel(video ? "vj" : "aj");
            filters.Add(labels.Count == 1
                ? $"[{labels[0]}]{(video ? "null" : "anull")}[{output}]"
                : $"{string.Concat(labels.Select(l => $"[{l}]"))}concat=n={labels.Count}:v={(video ? 1 : 0)}:a={(video ? 0 : 1)}[{output}]");
            return output;
        }

        string BlackPicture()
        {
            var label = NewLabel("g");
            filters.Add($"color=c=black:s={size}:r={fps}:d={Seconds(duration)},format=yuv420p,setsar=1[{label}]");
            return label;
        }

        // ---- Text: drawtext reads each text from a file and its font from a copy, both next to
        // FFmpeg (it runs in a work folder), so neither text nor paths need filter-graph escaping.
        findFont ??= FontLocator.Find;
        var workFiles = new List<ExportWorkFile>();
        var fontFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scale = height / 1080.0; // text sizes are stored for a 1080-high frame

        string FontFileFor(string family)
        {
            if (fontFiles.TryGetValue(family, out var name))
                return name;
            var path = findFont(family)
                       ?? throw new ExportException($"The font \"{family}\" used by a text clip isn't installed on this computer. Pick another font for that text.");
            name = $"font{fontFiles.Count}{Path.GetExtension(path).ToLowerInvariant()}";
            workFiles.Add(new ExportWorkFile(name, CopyFrom: path));
            fontFiles[family] = name;
            return name;
        }

        string DrawText(Clip clip, TextStyle style)
        {
            var textFile = $"text{workFiles.Count(f => f.Text is not null)}.txt";
            workFiles.Add(new ExportWorkFile(textFile, Text: style.Text.Replace("\r\n", "\n").TrimEnd('\r', '\n')));

            var start = Seconds(clip.Start);
            var end = Seconds(clip.End);
            var fontSize = Math.Max(1, (int)Math.Round(style.FontSize * scale));
            var options = new List<string>
            {
                $"fontfile={FontFileFor(style.FontFamily)}",
                $"textfile={textFile}",
                "expansion=none", // a % in the text is just a %
                $"fontsize={fontSize}",
                $"fontcolor={FFmpegColor(style.Color)}",
                // Line up the lines of multi-line text like the text is placed (FFmpeg 7+).
                $"text_align={style.LineAlignment switch { TextLineAlignment.Left => "L", TextLineAlignment.Right => "R", _ => "C" }}",
            };
            if (style.OutlineWidth > 0)
            {
                options.Add($"borderw={Math.Max(1, (int)Math.Round(style.OutlineWidth * scale))}");
                options.Add($"bordercolor={FFmpegColor(style.OutlineColor)}");
            }

            // Position = place within the room left around the text (0 = left/top edge, 1 = right/bottom).
            options.Add($"x=(w-text_w)*{Number(style.X)}");
            options.Add($"y=(h-text_h)*{Number(style.Y)}");
            options.Add($"enable='gte(t,{start})*lt(t,{end})'");

            // Fades: opacity ramps over the fade-in/out, like a video clip's fade.
            var ramps = new List<string>();
            if (clip.FadeIn > TimeSpan.Zero)
                ramps.Add($"(t-{start})/{Seconds(clip.FadeIn)}");
            if (clip.FadeOut > TimeSpan.Zero)
                ramps.Add($"({end}-t)/{Seconds(clip.FadeOut)}");
            if (ramps.Count > 0)
                options.Add($"alpha='clip({(ramps.Count == 1 ? ramps[0] : $"min({ramps[0]},{ramps[1]})")},0,1)'");

            return "drawtext=" + string.Join(":", options);
        }

        // ---- Video tracks, bottom to top. The bottom one is opaque; the others keep transparency.
        // A track's text is drawn right after its video, so higher tracks still cover it.
        var videoTracks = project.VideoTracks.AsEnumerable().Reverse()
            .Where(t => !t.Muted && t.Clips.Count > 0)
            .ToList();

        string? picture = null;
        for (var t = 0; t < videoTracks.Count; t++)
        {
            var texts = videoTracks[t].Clips
                .Where(c => c.Text is { Text.Length: > 0 } style && !string.IsNullOrWhiteSpace(style.Text))
                .OrderBy(c => c.Start)
                .ToList();
            var videoClips = videoTracks[t].Clips.Where(c => !c.IsText).OrderBy(c => c.Start).ToList();
            if (videoClips.Count > 0)
                picture = AddVideoTrack(videoClips, picture);

            if (texts.Count > 0)
            {
                picture ??= BlackPicture();
                var withText = NewLabel("txt");
                filters.Add($"[{picture}]{string.Join(",", texts.Select(c => DrawText(c, c.Text!)))}[{withText}]");
                picture = withText;
            }
        }

        // Lays out one track's video clips (with gaps) and stacks it on what's below.
        string AddVideoTrack(List<Clip> clips, string? below)
        {
            var isBottom = below is null;
            var pixelFormat = isBottom ? "yuv420p" : "yuva420p";
            var gapColor = isBottom ? "black" : "black@0";
            var alpha = isBottom ? "" : ":alpha=1"; // upper tracks fade to transparent, the bottom one to black

            string Gap(TimeSpan length)
            {
                var label = NewLabel("g");
                filters.Add($"color=c={gapColor}:s={size}:r={fps}:d={Seconds(length)},format={pixelFormat},setsar=1[{label}]");
                return label;
            }

            var segments = new List<string>();
            var position = TimeSpan.Zero;
            foreach (var clip in clips)
            {
                if (clip.Start > position)
                    segments.Add(Gap(clip.Start - position));

                var input = AddInput(clip, video: true);
                var label = NewLabel("v");
                var length = Seconds(clip.Length);
                // format before pad, so letterbox bars on upper tracks stay see-through.
                var chain =
                    $"[{input}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2," +
                    $"format={pixelFormat},pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color={gapColor},setsar=1," +
                    $"fps={fps},trim=duration={length},setpts=PTS-STARTPTS";
                if (clip.FadeIn > TimeSpan.Zero)
                    chain += $",fade=t=in:st=0:d={Seconds(clip.FadeIn)}{alpha}";
                if (clip.FadeOut > TimeSpan.Zero)
                    chain += $",fade=t=out:st={Seconds(clip.Length - clip.FadeOut)}:d={Seconds(clip.FadeOut)}{alpha}";
                filters.Add($"{chain}[{label}]");

                segments.Add(label);
                position = clip.End;
            }

            if (position < duration)
                segments.Add(Gap(duration - position));

            var track = Join(segments, video: true);
            if (below is null)
                return track;

            var stacked = NewLabel("o");
            filters.Add($"[{below}][{track}]overlay=format=auto:eof_action=pass[{stacked}]");
            return stacked;
        }

        // Only sound (or every video track hidden): black picture.
        picture ??= BlackPicture();

        filters.Add(isVaapi ? $"[{picture}]format=nv12,hwupload[vout]" : $"[{picture}]format=nv12[vout]");

        // ---- Audio tracks: each laid out with silent gaps, then all mixed.
        string Silence(TimeSpan length)
        {
            var label = NewLabel("s");
            filters.Add($"anullsrc=r={sampleRate}:cl={layout},atrim=duration={Seconds(length)}[{label}]");
            return label;
        }

        var mix = new List<string>();
        foreach (var track in project.AudioTracks)
        {
            if (track.Muted || track.Clips.Count == 0 || track.Volume <= 0)
                continue;

            var segments = new List<string>();
            var position = TimeSpan.Zero;
            foreach (var clip in track.Clips.OrderBy(c => c.Start))
            {
                if (clip.Start > position)
                    segments.Add(Silence(clip.Start - position));

                var input = AddInput(clip, video: false);
                var label = NewLabel("a");
                var length = Seconds(clip.Length);
                var chain = $"[{input}:a:0]{audioFormat},apad=whole_dur={length},atrim=duration={length},asetpts=PTS-STARTPTS";
                if (clip.FadeIn > TimeSpan.Zero)
                    chain += $",afade=t=in:st=0:d={Seconds(clip.FadeIn)}";
                if (clip.FadeOut > TimeSpan.Zero)
                    chain += $",afade=t=out:st={Seconds(clip.Length - clip.FadeOut)}:d={Seconds(clip.FadeOut)}";
                filters.Add($"{chain}[{label}]");

                segments.Add(label);
                position = clip.End;
            }

            if (position < duration)
                segments.Add(Silence(duration - position));

            var joined = Join(segments, video: false);
            var withVolume = NewLabel("t");
            filters.Add($"[{joined}]volume={Gain(track)}[{withVolume}]");
            mix.Add(withVolume);
        }

        string audioOut;
        if (mix.Count == 0)
        {
            audioOut = Silence(duration);
        }
        else if (mix.Count == 1)
        {
            audioOut = mix[0];
        }
        else
        {
            audioOut = "aout";
            // normalize=0: keep each track's volume as set, instead of dividing by the number of tracks.
            filters.Add($"{string.Concat(mix.Select(l => $"[{l}]"))}amix=inputs={mix.Count}:duration=longest:normalize=0[{audioOut}]");
        }

        args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[vout]", "-map", $"[{audioOut}]"]);
        args.AddRange(VideoEncoderArguments(encoder.Encoder, quality));
        args.AddRange(["-c:a", "aac", "-b:a", "192k", "-t", Seconds(duration)]);

        var fullOutput = Path.GetFullPath(outputPath);
        var extension = Path.GetExtension(fullOutput).ToLowerInvariant();
        if (extension is ".mp4" or ".mov" or ".m4v")
        {
            args.AddRange(["-movflags", "+faststart"]); // playable while still downloading
            if (encoder.Encoder.Codec == VideoCodec.Hevc)
                args.AddRange(["-tag:v", "hvc1"]);      // needed for Apple players to accept HEVC
        }

        var tempOutput = Path.Combine(
            Path.GetDirectoryName(fullOutput)!,
            $"{Path.GetFileNameWithoutExtension(fullOutput)}.csnlved-tmp{extension}");
        args.Add(tempOutput);

        return new ExportPlan(args, fullOutput, tempOutput, duration, inputFiles.Distinct().ToList(), encoder.Encoder.FfmpegName, workFiles);
    }

    /// <summary>Constant-quality settings per GPU API. Lower number = better quality, bigger file.</summary>
    internal static IEnumerable<string> VideoEncoderArguments(HardwareEncoder encoder, ExportQuality quality)
    {
        var q = quality switch
        {
            ExportQuality.High => 19,
            ExportQuality.Small => 28,
            _ => 23,
        };
        var qs = q.ToString(CultureInfo.InvariantCulture);
        var name = encoder.FfmpegName;

        switch (encoder.Api)
        {
            case GpuApi.Nvenc:
                return ["-c:v", name, "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", qs, "-b:v", "0"];

            case GpuApi.Amf:
                var amf = new List<string> { "-c:v", name, "-quality", "quality", "-rc", "cqp", "-qp_i", qs, "-qp_p", qs };
                if (encoder.Codec == VideoCodec.H264)
                    amf.AddRange(["-qp_b", qs]);
                return amf;

            case GpuApi.Qsv:
                return ["-c:v", name, "-preset", "medium", "-global_quality", qs];

            case GpuApi.Vaapi:
                // AV1 over VAAPI uses a 0–255 scale instead of 0–51.
                var qp = encoder.Codec == VideoCodec.Av1 ? Math.Min(255, q * 4) : q;
                return ["-c:v", name, "-rc_mode", "CQP", "-qp", qp.ToString(CultureInfo.InvariantCulture)];

            case GpuApi.VideoToolbox:
                // VideoToolbox: 1–100, higher = better.
                var vq = quality switch
                {
                    ExportQuality.High => 70,
                    ExportQuality.Small => 45,
                    _ => 58,
                };
                return ["-c:v", name, "-q:v", vq.ToString(CultureInfo.InvariantCulture)];

            default:
                throw new ExportException($"Unsupported encoder {name}.");
        }
    }

    private static string Gain(Track track) =>
        (track.Muted ? 0.0 : track.Volume).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Seconds with a dot, whatever the computer's language (Romanian would write a comma).</summary>
    internal static string Seconds(TimeSpan time) =>
        time.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>"#RRGGBB" / "#RRGGBBAA" → FFmpeg's "0xRRGGBB" / "0xRRGGBBAA".</summary>
    internal static string FFmpegColor(string color) =>
        ProjectEditor.IsColor(color) ? "0x" + color[1..].ToUpperInvariant() : "0xFFFFFF";
}
