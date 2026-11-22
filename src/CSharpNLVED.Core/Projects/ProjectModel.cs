using System.Text.Json.Serialization;

namespace CSharpNLVED.Core.Projects;

// The project model (format version 2), Vegas-style:
//  * Any number of video tracks and audio tracks. Video tracks higher in the list
//    are drawn on top of the ones below.
//  * Every clip sits at its own position (Start) on its track. Gaps are allowed.
//    Two neighbouring clips on a track may overlap: the overlap is a transition
//    (crossfade or wipe) from the earlier clip to the later one, like in Vegas.
//    Never more than two clips at once, and a clip never sits entirely inside another.
//  * Importing a video with sound creates two clips, one on a video track and one
//    on an audio track, that share a LinkId: they move, trim and split together
//    until they're unlinked.
//  * Each clip can fade in and out.

public enum TrackKind
{
    Video,
    Audio,
}

internal static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N");
}

/// <summary>A fraction, used for frame rates like 30000/1001 (29.97 fps).</summary>
public readonly record struct Rational(int Numerator, int Denominator)
{
    [JsonIgnore]
    public double Value => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    public override string ToString() => Denominator == 1 ? $"{Numerator}" : $"{Numerator}/{Denominator}";
}

public sealed class ProjectSettings
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public Rational FrameRate { get; set; } = new(30, 1);
    public int SampleRate { get; set; } = 48000;
    public int AudioChannels { get; set; } = 2;

    /// <summary>If true, the first video clip added sets size and frame rate.</summary>
    public bool MatchFirstClip { get; set; } = true;

    /// <summary>Length of one frame; the shortest a clip can be.</summary>
    [JsonIgnore]
    public TimeSpan FrameDuration => FrameRate.Numerator > 0
        ? TimeSpan.FromSeconds((double)FrameRate.Denominator / FrameRate.Numerator)
        : TimeSpan.FromSeconds(1.0 / 30);
}

/// <summary>A source file used in the project. Its details come from ffprobe.</summary>
public sealed class MediaItem
{
    public string Id { get; set; } = Ids.New();

    /// <summary>Absolute path to the file.</summary>
    public string Path { get; set; } = "";

    /// <summary>Path relative to the project file, so a project folder can be moved or put on a USB stick.</summary>
    public string? RelativePath { get; set; }

    public TimeSpan Duration { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public Rational? FrameRate { get; set; }

    /// <summary>True when the file couldn't be found on load.</summary>
    [JsonIgnore]
    public bool IsMissing { get; set; }

    [JsonIgnore]
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// What a text clip shows and how. A record, so <c>style with { Text = "…" }</c> makes a
/// changed copy and two styles compare by value.
///
/// Sizes are in pixels of a 1080-pixel-high frame and are scaled to the export size, so a
/// title looks the same at 720p, 1080p or 1600p.
///
/// X and Y place the text within the room the frame leaves around it: 0 = against the
/// left/top edge, 1 = against the right/bottom edge, 0.5 = centred. So text never runs off
/// the frame, and corner positions work whatever the text's length.
/// </summary>
public sealed record TextStyle
{
    public const string DefaultFont = "Arial Black"; // the owner's favourite

    public string Text { get; set; } = "Text";
    public string FontFamily { get; set; } = DefaultFont;
    public double FontSize { get; set; } = 96;

    /// <summary>"#RRGGBB" or "#RRGGBBAA" (AA = opacity, FF = solid).</summary>
    public string Color { get; set; } = "#FFFFFF";

    public string OutlineColor { get; set; } = "#000000";

    /// <summary>0 = no outline.</summary>
    public double OutlineWidth { get; set; } = 4;

    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;

    /// <summary>How the lines of multi-line text line up: follows the horizontal position.</summary>
    [JsonIgnore]
    public TextLineAlignment LineAlignment =>
        X < 1.0 / 3 ? TextLineAlignment.Left : X > 2.0 / 3 ? TextLineAlignment.Right : TextLineAlignment.Center;
}

public enum TransitionKind
{
    /// <summary>The two pictures blend evenly.</summary>
    Crossfade,
}

/// <summary>The look of a transition. Its length is the overlap of the two clips.</summary>
public sealed record Transition
{
    public TransitionKind Kind { get; set; } = TransitionKind.Crossfade;
}

public enum TextLineAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>Plays MediaItem from In to Out (or shows <see cref="Text"/>), placed on the timeline at Start.</summary>
public sealed class Clip
{
    public string Id { get; set; } = Ids.New();
    public string MediaId { get; set; } = "";

    /// <summary>Where the clip begins on the timeline.</summary>
    public TimeSpan Start { get; set; }

    /// <summary>The part of the media file that plays.</summary>
    public TimeSpan In { get; set; }

    public TimeSpan Out { get; set; }

    /// <summary>Clips with the same LinkId (e.g. a video and its sound) move, trim and split together.</summary>
    public string? LinkId { get; set; }

    public TimeSpan FadeIn { get; set; }
    public TimeSpan FadeOut { get; set; }

    /// <summary>
    /// Set for a text clip: a title drawn on a video track instead of a media file.
    /// A text clip has no MediaId, In is always 0 and Out is its length.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextStyle? Text { get; set; }

    [JsonIgnore]
    public bool IsText => Text is not null;

    /// <summary>
    /// How this clip comes in over the previous clip where they overlap (a transition).
    /// The overlap itself sets the length; null = crossfade. Cleared when the overlap goes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Transition? Transition { get; set; }

    [JsonIgnore]
    public TimeSpan Length => Out - In;

    [JsonIgnore]
    public TimeSpan End => Start + Length;
}

public sealed class Track
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public TrackKind Kind { get; set; }

    /// <summary>Audio tracks: linear gain, 1.0 = 100 % (0 dB). Not used for video tracks.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Muted audio track = silent; muted video track = hidden.</summary>
    public bool Muted { get; set; }

    /// <summary>Always kept sorted by Start.</summary>
    public List<Clip> Clips { get; set; } = [];

    /// <summary>Where the last clip ends.</summary>
    [JsonIgnore]
    public TimeSpan Duration => Clips.Count == 0 ? TimeSpan.Zero : Clips.Max(c => c.End);

    public int IndexOf(string clipId) => Clips.FindIndex(c => c.Id == clipId);

    internal void SortClips() => Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
}

public sealed class Project
{
    /// <summary>3: transitions (overlapping clips). Older CSharpNLVED versions refuse these files instead of mishandling them.</summary>
    public const int CurrentFormatVersion = 3;

    /// <summary>File format version, so newer CSharpNLVED versions can upgrade old projects.</summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public ProjectSettings Settings { get; set; } = new();
    public List<MediaItem> Media { get; set; } = [];

    /// <summary>Top to bottom: the first track is drawn on top.</summary>
    public List<Track> VideoTracks { get; set; } = [];

    public List<Track> AudioTracks { get; set; } = [];

    /// <summary>Only present in version 1 files (one video track); converted on load.</summary>
    [JsonPropertyName("videoTrack")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Track? LegacyVideoTrack { get; set; }

    /// <summary>Video tracks (top to bottom), then audio tracks.</summary>
    [JsonIgnore]
    public IEnumerable<Track> AllTracks => VideoTracks.Concat(AudioTracks);

    /// <summary>Where the last clip on any track ends.</summary>
    [JsonIgnore]
    public TimeSpan Duration => AllTracks.Select(t => t.Duration).DefaultIfEmpty(TimeSpan.Zero).Max();

    public static Project CreateDefault() => new()
    {
        VideoTracks = { new Track { Name = "Video 1", Kind = TrackKind.Video } },
        AudioTracks =
        {
            new Track { Name = "Audio 1", Kind = TrackKind.Audio },
            new Track { Name = "Music", Kind = TrackKind.Audio },
        },
    };

    public MediaItem? FindMedia(string id) => Media.Find(m => m.Id == id);

    public Track? FindTrack(string id) => AllTracks.FirstOrDefault(t => t.Id == id);

    public (Track Track, Clip Clip)? FindClip(string clipId)
    {
        foreach (var track in AllTracks)
        {
            var clip = track.Clips.Find(c => c.Id == clipId);
            if (clip is not null)
                return (track, clip);
        }

        return null;
    }

    /// <summary>The clip plus every clip linked to it (just the clip if it isn't linked).</summary>
    public List<(Track Track, Clip Clip)> LinkedGroup(string clipId)
    {
        if (FindClip(clipId) is not { } found)
            return [];
        if (found.Clip.LinkId is not { } link)
            return [found];

        return AllTracks
            .SelectMany(t => t.Clips.Where(c => c.LinkId == link).Select(c => (t, c)))
            .ToList();
    }
}
