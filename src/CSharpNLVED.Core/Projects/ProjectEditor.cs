namespace CSharpNLVED.Core.Projects;

public enum ClipEdge
{
    Left,
    Right,
}

/// <summary>
/// The one place where a project gets changed. Every edit goes through here, so
/// undo/redo, the "unsaved changes" flag and change notifications always work.
///
/// Undo keeps snapshots of the whole project as JSON. Projects are small (a few KB),
/// so this is simple and can't get out of sync the way per-operation undo can.
/// Every edit is also checked afterwards (no overlapping clips, valid trim points);
/// an edit that would break the project is rolled back and reported.
///
/// After undo/redo, <see cref="Project"/> is a new object: always look tracks and
/// clips up again by id instead of holding on to them.
/// </summary>
public sealed class ProjectEditor
{
    private const int MaxUndo = 200;

    /// <summary>Repeated edits with the same key within this window (e.g. dragging) are one undo step.</summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private readonly List<(string Json, string Description)> _undo = [];
    private readonly List<(string Json, string Description)> _redo = [];
    private string? _lastCoalesceKey;
    private DateTime _lastEditUtc;

    private ProjectEditor(Project project, string? filePath)
    {
        Project = project;
        FilePath = filePath;
    }

    public Project Project { get; private set; }
    public string? FilePath { get; private set; }
    public bool IsDirty { get; private set; }

    /// <summary>
    /// When on, deleting or trimming a clip moves the clips after it on the same track(s)
    /// to close or open the gap, like Vegas's Auto Ripple. Off by default.
    /// </summary>
    public bool AutoRipple { get; set; }

    public string DisplayName => FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => CanUndo ? _undo[^1].Description : null;
    public string? RedoDescription => CanRedo ? _redo[^1].Description : null;

    private TimeSpan MinLength => Project.Settings.FrameDuration;

    /// <summary>Raised after any change: edits, undo/redo and saving.</summary>
    public event EventHandler? Changed;

    // ================================================================= files

    public static ProjectEditor CreateNew() => new(Project.CreateDefault(), null);

    public static async Task<ProjectEditor> OpenAsync(string path, CancellationToken ct = default)
    {
        var project = await ProjectFile.LoadAsync(path, ct).ConfigureAwait(false);
        return new ProjectEditor(project, Path.GetFullPath(path));
    }

    /// <param name="path">Where to save; null saves to the current file.</param>
    public async Task SaveAsync(string? path = null, CancellationToken ct = default)
    {
        var target = path ?? FilePath ?? throw new InvalidOperationException("Choose where to save the project first.");

        // No ConfigureAwait(false): Changed must be raised on the caller's (UI) thread.
        await ProjectFile.SaveAsync(Project, target, ct);

        FilePath = Path.GetFullPath(target);
        IsDirty = false;
        _lastCoalesceKey = null;
        OnChanged();
    }

    // ================================================================= media

    /// <summary>Adds a media file to the project. Adding the same file twice returns the existing item.</summary>
    public MediaItem AddMedia(MediaItem item)
    {
        var existing = Project.Media.Find(m => string.Equals(m.Path, item.Path, PathComparison));
        if (existing is not null)
            return existing;

        Edit($"Add {item.FileName}", () => Project.Media.Add(item));
        return item;
    }

    /// <summary>Removes a media file and every clip that uses it.</summary>
    public void RemoveMedia(string mediaId)
    {
        var media = GetMedia(mediaId);
        Edit($"Remove {media.FileName}", () =>
        {
            foreach (var track in Project.AllTracks)
                track.Clips.RemoveAll(c => c.MediaId == mediaId);
            Project.Media.Remove(media);
        });
    }

    // =========================================================== placing clips

    /// <summary>
    /// Puts a whole media file on the timeline. A video with sound becomes a linked pair:
    /// a clip on a video track and one on an audio track. Without a position, it goes
    /// after the last clip on those tracks.
    /// </summary>
    /// <param name="videoTrackId">Video track to use (default: the top one).</param>
    /// <param name="audioTrackId">
    /// Audio track to use (default: the first audio track for a video's sound,
    /// the last audio track for audio-only files such as music).
    /// </param>
    public IReadOnlyList<Clip> AddToTimeline(string mediaId, TimeSpan? position = null,
        string? videoTrackId = null, string? audioTrackId = null)
    {
        var media = GetMedia(mediaId);
        if (media.Duration <= TimeSpan.Zero)
            throw new InvalidOperationException($"The length of {media.FileName} is unknown.");
        if (!media.HasVideo && !media.HasAudio)
            throw new InvalidOperationException($"{media.FileName} has no video or audio.");

        var createAudioTrack = media.HasAudio && Project.AudioTracks.Count == 0;
        var newAudioTrack = createAudioTrack ? new Track { Name = "Audio 1", Kind = TrackKind.Audio } : null;

        var videoTrack = media.HasVideo
            ? RequireKind(videoTrackId is null ? Project.VideoTracks[0] : GetTrack(videoTrackId), TrackKind.Video)
            : null;
        var audioTrack = !media.HasAudio ? null
            : newAudioTrack
              ?? (audioTrackId is not null ? RequireKind(GetTrack(audioTrackId), TrackKind.Audio)
                  : media.HasVideo ? Project.AudioTracks[0]
                  : Project.AudioTracks[^1]);

        var length = media.Duration;
        var start = position is { } p
            ? FirstFreeFrom(Max(p, TimeSpan.Zero), length, videoTrack, audioTrack)
            : Max(videoTrack?.Duration ?? TimeSpan.Zero, audioTrack?.Duration ?? TimeSpan.Zero);

        var firstVideoClip = media.HasVideo && !Project.VideoTracks.Any(t => t.Clips.Count > 0);
        var link = videoTrack is not null && audioTrack is not null ? Ids.New() : null;
        var created = new List<Clip>();

        Edit($"Add {media.FileName}", () =>
        {
            if (newAudioTrack is not null)
                Project.AudioTracks.Add(newAudioTrack);

            foreach (var track in new[] { videoTrack, audioTrack })
            {
                if (track is null)
                    continue;
                var clip = new Clip { MediaId = media.Id, Start = start, In = TimeSpan.Zero, Out = length, LinkId = link };
                track.Clips.Add(clip);
                track.SortClips();
                created.Add(clip);
            }

            // The first video clip decides the export size and frame rate.
            if (firstVideoClip && Project.Settings.MatchFirstClip && media is { Width: > 1, Height: > 1 })
                MatchFormatTo(media);
        });

        return created;
    }

    /// <summary>
    /// The earliest time at or after <paramref name="from"/> where a clip of this length fits
    /// on all the given tracks (dropping onto an occupied spot puts it in the next free space).
    /// </summary>
    private static TimeSpan FirstFreeFrom(TimeSpan from, TimeSpan length, params Track?[] tracks)
    {
        var used = tracks.OfType<Track>().ToList();
        var candidates = used.SelectMany(t => t.Clips.Select(c => c.End)).Where(end => end > from).Append(from);

        foreach (var candidate in candidates.Distinct().Order())
        {
            if (used.All(t => IsFree(t, candidate, length, null)))
                return candidate;
        }

        return used.Select(t => t.Duration).DefaultIfEmpty(TimeSpan.Zero).Max(); // after everything
    }

    private void MatchFormatTo(MediaItem media)
    {
        var settings = Project.Settings;
        settings.Width = media.Width!.Value & ~1; // even sizes only
        settings.Height = media.Height!.Value & ~1;

        // Some files report silly rates (e.g. 90000/1); only accept 1–240 fps.
        if (media.FrameRate is { Numerator: > 0, Denominator: > 0 } rate && rate.Value is >= 1 and <= 240)
            settings.FrameRate = rate;
    }

    // ================================================================= text

    /// <summary>Default length of a new text clip.</summary>
    public static readonly TimeSpan DefaultTextLength = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Adds a text clip on a video track (default: the top one). With a position it goes in the
    /// first free spot at or after it; without, after the last clip on that track.
    /// </summary>
    public Clip AddText(TimeSpan? position = null, string? videoTrackId = null, TextStyle? style = null, TimeSpan? length = null)
    {
        var track = RequireKind(videoTrackId is null ? Project.VideoTracks[0] : GetTrack(videoTrackId), TrackKind.Video);
        var clipLength = Max(length ?? DefaultTextLength, MinLength);
        var text = Checked(style ?? new TextStyle());
        var start = position is { } p ? FirstFreeFrom(Max(p, TimeSpan.Zero), clipLength, track) : track.Duration;

        var clip = new Clip { Start = start, In = TimeSpan.Zero, Out = clipLength, Text = text };
        Edit("Add text", () =>
        {
            track.Clips.Add(clip);
            track.SortClips();
        });
        return clip;
    }

    /// <summary>
    /// Adds a text clip at exactly <paramref name="position"/>, on top of whatever video is
    /// playing there (a title over the gameplay): on the nearest video track above the highest
    /// video clip at that spot that has room for it. If no track has room, a new "Text" track is
    /// added on top. Track and clip are one undo step.
    /// </summary>
    public Clip AddTextOverVideo(TimeSpan position, TextStyle? style = null, TimeSpan? length = null)
    {
        var start = Max(position, TimeSpan.Zero);
        var clipLength = Max(length ?? DefaultTextLength, MinLength);
        var text = Checked(style ?? new TextStyle());
        var tracks = Project.VideoTracks; // index 0 = top

        // The top-most track with video (not text) at the playhead: the text must go above it.
        var covered = tracks.FindIndex(t => t.Clips.Any(c => !c.IsText && c.Start <= start && start < c.End));
        var below = covered >= 0 ? covered : tracks.Count;
        Track? target = null;
        for (var i = below - 1; i >= 0 && target is null; i--) // nearest above first
        {
            if (IsFree(tracks[i], start, clipLength, null))
                target = tracks[i];
        }

        var newTrack = target is null ? new Track { Name = NewTrackName("Text"), Kind = TrackKind.Video } : null;
        var clip = new Clip { Start = start, In = TimeSpan.Zero, Out = clipLength, Text = text };
        Edit("Add text", () =>
        {
            if (newTrack is not null)
                Project.VideoTracks.Insert(0, newTrack);
            var track = target ?? newTrack!;
            track.Clips.Add(clip);
            track.SortClips();
        });
        return clip;
    }

    /// <summary>"Text", or "Text 2", "Text 3"… if that name is taken.</summary>
    private string NewTrackName(string baseName)
    {
        var names = Project.AllTracks.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
            return baseName;
        var n = 2;
        while (names.Contains($"{baseName} {n}"))
            n++;
        return $"{baseName} {n}";
    }

    /// <summary>Changes what a text clip shows (text, font, colours, position…). Repeated changes (typing, sliders) are one undo step.</summary>
    public void SetTextStyle(string clipId, TextStyle style)
    {
        var (_, clip) = GetClip(clipId);
        if (clip.Text is null)
            throw new InvalidOperationException("That clip isn't a text clip.");

        var text = Checked(style);
        if (text == clip.Text)
            return; // unchanged: no undo step
        Edit("Edit text", () => clip.Text = text, coalesceKey: $"text:{clipId}");
    }

    /// <summary>A copy of the style with sizes and position kept in sensible ranges; refuses invalid colours.</summary>
    private static TextStyle Checked(TextStyle style)
    {
        foreach (var (name, color) in new[] { ("text colour", style.Color), ("outline colour", style.OutlineColor) })
        {
            if (!IsColor(color))
                throw new InvalidOperationException($"The {name} \"{color}\" isn't a colour like #FFFFFF.");
        }

        static double Fix(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

        return style with
        {
            Text = style.Text ?? "",
            FontFamily = string.IsNullOrWhiteSpace(style.FontFamily) ? TextStyle.DefaultFont : style.FontFamily.Trim(),
            Color = style.Color.ToUpperInvariant(),
            OutlineColor = style.OutlineColor.ToUpperInvariant(),
            FontSize = Fix(style.FontSize, 4, 1000, 96),
            OutlineWidth = Fix(style.OutlineWidth, 0, 50, 0),
            X = Fix(style.X, 0, 1, 0.5),
            Y = Fix(style.Y, 0, 1, 0.5),
        };
    }

    /// <summary>"#RRGGBB" or "#RRGGBBAA".</summary>
    public static bool IsColor(string? value) =>
        value is { Length: 7 or 9 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    // ============================================================== moving

    /// <summary>
    /// Moves a clip (and the clips linked to it) so it starts at <paramref name="newStart"/>,
    /// optionally onto another track of the same kind. If that spot is taken, the nearest
    /// free spot is used. Dragging is merged into one undo step.
    /// </summary>
    /// <returns>False if the clip couldn't be moved anywhere near there.</returns>
    public bool MoveClip(string clipId, TimeSpan newStart, string? targetTrackId = null)
    {
        var (track, clip) = GetClip(clipId);
        var target = targetTrackId is null ? track : GetTrack(targetTrackId);
        if (target.Kind != track.Kind)
            return false;

        // The group as it would be: the dragged clip on the target track, linked clips on their own.
        var group = Project.LinkedGroup(clipId)
            .Select(g => g.Clip.Id == clipId ? (Track: target, g.Clip) : g)
            .ToList();

        if (BestShift(group, newStart - clip.Start) is not { } shift)
            return false;
        if (shift == TimeSpan.Zero && target == track)
            return true;

        Edit("Move clip", () =>
        {
            if (target != track)
            {
                track.Clips.Remove(clip);
                target.Clips.Add(clip);
            }

            foreach (var (t, c) in group)
            {
                c.Start += shift;
                t.SortClips();
            }
        }, coalesceKey: $"move:{clipId}");
        return true;
    }

    /// <summary>
    /// The shift closest to <paramref name="requested"/> that lets every clip of the group
    /// land in free space on its track (or null if none of the candidates work).
    /// </summary>
    private static TimeSpan? BestShift(List<(Track Track, Clip Clip)> group, TimeSpan requested)
    {
        var moving = group.Select(g => g.Clip.Id).ToHashSet();
        var earliest = group.Min(g => g.Clip.Start);
        if (requested < -earliest)
            requested = -earliest; // nothing may start before 0:00

        var candidates = new List<TimeSpan> { requested, TimeSpan.Zero };
        foreach (var (track, clip) in group)
        {
            foreach (var (gapStart, gapEnd) in FreeGaps(track, moving))
            {
                if (gapEnd - gapStart < clip.Length)
                    continue;
                var latest = gapEnd == TimeSpan.MaxValue ? TimeSpan.MaxValue : gapEnd - clip.Length;
                candidates.Add(Clamp(clip.Start + requested, gapStart, latest) - clip.Start);
            }
        }

        foreach (var shift in candidates.Distinct().OrderBy(s => (s - requested).Duration()))
        {
            if (shift >= -earliest && group.All(g => IsFree(g.Track, g.Clip.Start + shift, g.Clip.Length, moving)))
                return shift;
        }

        return null;
    }

    // ============================================================== trimming

    /// <summary>
    /// Drags a clip's left or right edge to <paramref name="edgePosition"/> on the timeline.
    /// Linked clips are trimmed the same way. Limited by the media's length, the neighbouring
    /// clips (unless auto-ripple is on, which shifts later clips instead) and a one-frame minimum.
    /// </summary>
    public void TrimEdgeTo(string clipId, ClipEdge edge, TimeSpan edgePosition)
    {
        var (_, clip) = GetClip(clipId);
        var requested = edgePosition - (edge == ClipEdge.Left ? clip.Start : clip.End);
        var group = Project.LinkedGroup(clipId);
        var moving = group.Select(g => g.Clip.Id).ToHashSet();

        // Allowed range for the edge shift d, intersected over the whole group.
        var lo = TimeSpan.MinValue;
        var hi = TimeSpan.MaxValue;
        foreach (var (track, c) in group)
        {
            // Text clips have no media, so they can be made as long as there's room.
            var mediaDuration = c.IsText ? (TimeSpan?)null : GetMedia(c.MediaId).Duration;
            var prev = Previous(track, c, moving);
            var next = Next(track, c, moving);
            var incoming = prev is not null && prev.End > c.Start; // a transition in from prev
            var outgoing = next is not null && next.Start < c.End; // a transition out to next
            if (edge == ClipEdge.Left)
            {
                if (!c.IsText)
                    lo = Max(lo, -c.In);                   // can't start before the media does
                hi = Min(hi, c.Length - MinLength);        // keep at least one frame
                if (outgoing)
                {
                    // Keep a frame of c before the next clip comes in (with ripple, both transitions must fit).
                    hi = Min(hi, AutoRipple
                        ? c.Length - Overlap(prev, c) - Overlap(c, next) - MinLength
                        : next!.Start - c.Start - MinLength);
                }

                if (!AutoRipple)
                {
                    if (prev is null)
                        lo = Max(lo, -c.Start);            // not before 0:00
                    else if (incoming)
                        lo = Max(lo, prev.Start + Overlap(Previous(track, prev, moving), prev) + MinLength - c.Start); // deepen the transition, keep a frame of prev
                    else
                        lo = Max(lo, prev.End - c.Start);  // don't run into the clip before
                }
            }
            else
            {
                if (mediaDuration is { } duration)
                    hi = Min(hi, duration - c.Out);        // can't go past the media's end
                lo = Max(lo, MinLength - c.Length);
                if (incoming)
                    lo = Max(lo, prev!.End + MinLength - c.End); // stay past the clip before
                if (!AutoRipple && next is not null)
                {
                    hi = Min(hi, outgoing
                        ? next.End - Overlap(next, Next(track, next, moving)) - MinLength - c.End // deepen, keep a frame of next
                        : next.Start - c.End);             // don't run into the clip after
                }
            }
        }

        if (lo > hi)
            return;
        var d = Clamp(requested, lo, hi);
        if (d == TimeSpan.Zero)
            return;

        Edit("Trim clip", () =>
        {
            foreach (var (track, c) in group)
            {
                var oldEnd = c.End;
                if (edge == ClipEdge.Left)
                {
                    if (c.IsText)
                        c.Out -= d; // text has no media offset: In stays 0
                    else
                        c.In += d;
                    if (!AutoRipple)
                        c.Start += d; // the end stays put
                    c.FadeIn = Min(c.FadeIn, c.Length);
                }
                else
                {
                    c.Out += d;
                    c.FadeOut = Min(c.FadeOut, c.Length);
                }

                if (AutoRipple)
                    ShiftLaterClips(track, c.Start, c.End - oldEnd, moving); // with ripple the start stays put
            }
        }, coalesceKey: $"trim:{clipId}:{edge}");
    }

    // ================================================================= fades

    /// <summary>
    /// Sets a clip's fade-in (<see cref="ClipEdge.Left"/>) or fade-out (<see cref="ClipEdge.Right"/>)
    /// length, like dragging a clip's top corner in Vegas. Linked clips get the same fade, so a
    /// video and its sound fade together. Each clip's fade is limited to 0 and to the part of the
    /// clip not already used by its other fade. Dragging is merged into one undo step.
    /// </summary>
    public void SetFade(string clipId, ClipEdge edge, TimeSpan length)
    {
        GetClip(clipId); // throws for an unknown id
        var group = Project.LinkedGroup(clipId);

        TimeSpan Limited(Clip c)
        {
            var room = c.Length - (edge == ClipEdge.Left ? c.FadeOut : c.FadeIn);
            return Clamp(length, TimeSpan.Zero, Max(room, TimeSpan.Zero));
        }

        if (group.All(g => (edge == ClipEdge.Left ? g.Clip.FadeIn : g.Clip.FadeOut) == Limited(g.Clip)))
            return; // unchanged: no undo step

        Edit(edge == ClipEdge.Left ? "Fade in" : "Fade out", () =>
        {
            foreach (var (_, c) in group)
            {
                if (edge == ClipEdge.Left)
                    c.FadeIn = Limited(c);
                else
                    c.FadeOut = Limited(c);
            }
        }, coalesceKey: $"fade:{clipId}:{edge}");
    }

    // =========================================================== transitions

    /// <summary>Default length of a new transition.</summary>
    public static readonly TimeSpan DefaultTransitionLength = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The transition into this clip from the clip before it on its track: how long the two
    /// overlap and how it looks. Null if they don't overlap.
    /// </summary>
    public (TimeSpan Length, Transition Style)? TransitionInto(string clipId)
    {
        var (track, clip) = GetClip(clipId);
        var overlap = Overlap(Previous(track, clip, null), clip);
        return overlap > TimeSpan.Zero ? (overlap, clip.Transition ?? new Transition()) : null;
    }

    /// <summary>
    /// The longest transition possible into this clip (each clip keeps at least a frame of its
    /// own, and its transitions on both ends can't cross), or zero if there's no clip before it.
    /// </summary>
    public TimeSpan MaxTransitionInto(string clipId)
    {
        var (track, clip) = GetClip(clipId);
        if (Previous(track, clip, null) is not { } prev)
            return TimeSpan.Zero;

        var roomInPrev = prev.Length - Overlap(Previous(track, prev, null), prev);
        var roomInClip = clip.Length - Overlap(clip, Next(track, clip, null));
        return Max(Min(roomInPrev, roomInClip) - MinLength, TimeSpan.Zero);
    }

    /// <summary>
    /// Makes (or changes, or with zero removes) the transition from the previous clip on the
    /// track into this one. The clips have to touch (or already overlap). This clip, the clips
    /// linked to it and everything after them on those tracks slide earlier or later together,
    /// so the rest of the edit (and the sound) stays in step, like Vegas's ripple. Dragging the
    /// length is one undo step.
    /// </summary>
    public void SetTransitionLength(string clipId, TimeSpan length)
    {
        var (track, clip) = GetClip(clipId);
        var prev = Previous(track, clip, null)
                   ?? throw new InvalidOperationException($"There's no clip before this one on {track.Name} to make a transition with.");

        var gap = clip.Start - prev.End;
        if (gap > MinLength / 2)
            throw new InvalidOperationException("There's a gap between these clips. Move them together first: a transition needs two clips that touch.");
        if (prev.IsText || clip.IsText)
            throw new InvalidOperationException("Text clips can't have transitions. Use fades for text instead.");

        var max = MaxTransitionInto(clipId);
        if (length > TimeSpan.Zero && max < MinLength)
            throw new InvalidOperationException("These clips are too short for a transition.");

        var newLength = Clamp(length, TimeSpan.Zero, max);
        var shift = prev.End - newLength - clip.Start; // where the clip must start for that overlap
        if (shift == TimeSpan.Zero)
            return;

        var current = Overlap(prev, clip);
        var description = newLength == TimeSpan.Zero ? "Remove transition"
            : current == TimeSpan.Zero ? "Add transition"
            : "Transition length";

        Edit(description, () =>
        {
            // This clip's group and everything after it on those tracks, plus anything linked to those.
            var toShift = new HashSet<Clip>();
            foreach (var (t, member) in Project.LinkedGroup(clipId))
            {
                foreach (var later in t.Clips.Where(c => c.Start >= member.Start))
                {
                    foreach (var (_, linked) in Project.LinkedGroup(later.Id))
                        toShift.Add(linked);
                }
            }

            foreach (var c in toShift)
                c.Start += shift;
            foreach (var t in Project.AllTracks)
                t.SortClips();
        }, coalesceKey: current > TimeSpan.Zero && newLength > TimeSpan.Zero ? $"transition:{clipId}" : null); // only length drags merge
    }

    /// <summary>Adds a transition (default length, crossfade) from the previous clip into this one.</summary>
    public void AddTransition(string clipId, TimeSpan? length = null) =>
        SetTransitionLength(clipId, length ?? DefaultTransitionLength);

    /// <summary>Pulls the clips apart again so they just touch (the rest of the edit moves back).</summary>
    public void RemoveTransition(string clipId) => SetTransitionLength(clipId, TimeSpan.Zero);

    /// <summary>Changes how the transition into this clip looks (e.g. crossfade or a wipe).</summary>
    public void SetTransitionStyle(string clipId, Transition style)
    {
        var (track, clip) = GetClip(clipId);
        if (Overlap(Previous(track, clip, null), clip) <= TimeSpan.Zero)
            throw new InvalidOperationException("There's no transition into that clip.");
        if ((clip.Transition ?? new Transition()) == style)
            return; // unchanged: no undo step

        Edit("Transition style", () => clip.Transition = style with { }, coalesceKey: $"transition-style:{clipId}");
    }

    // ===================================================== cut, delete, links

    /// <summary>
    /// Cuts clips in two at <paramref name="position"/>. With clip ids, only those clips
    /// (and their linked clips) are cut; without, every clip under the position is.
    /// Second halves stay linked to each other. Returns the new second halves.
    /// </summary>
    public IReadOnlyList<Clip> Split(TimeSpan position, IEnumerable<string>? clipIds = null)
    {
        var candidates = clipIds is null
            ? Project.AllTracks.SelectMany(t => t.Clips.Select(c => (Track: t, Clip: c)))
            : clipIds.SelectMany(id => Project.LinkedGroup(id));

        var targets = candidates
            .DistinctBy(x => x.Clip.Id)
            .Where(x => CanSplitAt(x.Track, x.Clip, position))
            .ToList();
        if (targets.Count == 0)
            return [];

        var created = new List<Clip>();
        var newLinks = new Dictionary<string, string>();

        Edit("Split", () =>
        {
            foreach (var (track, clip) in targets)
            {
                var offset = position - clip.Start;
                string? link = null;
                if (clip.LinkId is { } oldLink && !newLinks.TryGetValue(oldLink, out link))
                {
                    link = Ids.New();
                    newLinks[oldLink] = link;
                }

                var second = new Clip
                {
                    MediaId = clip.MediaId,
                    Start = position,
                    In = clip.IsText ? TimeSpan.Zero : clip.In + offset,
                    Out = clip.IsText ? clip.Length - offset : clip.Out,
                    LinkId = link,
                    FadeOut = clip.FadeOut,
                    Text = clip.Text is null ? null : clip.Text with { },
                };
                clip.Out = clip.In + offset;
                clip.FadeOut = TimeSpan.Zero;
                clip.FadeIn = Min(clip.FadeIn, clip.Length);
                second.FadeOut = Min(second.FadeOut, second.Length);

                track.Clips.Add(second);
                track.SortClips();
                created.Add(second);
            }
        });

        return created;
    }

    /// <summary>At least a frame from each end, and not inside a transition.</summary>
    private bool CanSplitAt(Track track, Clip clip, TimeSpan position) =>
        position >= clip.Start + Overlap(Previous(track, clip, null), clip) + MinLength
        && position <= clip.End - Overlap(clip, Next(track, clip, null)) - MinLength;

    /// <summary>Removes clips and the clips linked to them. With auto-ripple, later clips move up.</summary>
    public void RemoveClips(IEnumerable<string> clipIds)
    {
        var targets = clipIds.SelectMany(id => Project.LinkedGroup(id)).DistinctBy(x => x.Clip.Id).ToList();
        if (targets.Count == 0)
            return;

        Edit(targets.Count == 1 ? "Remove clip" : $"Remove {targets.Count} clips", () =>
        {
            // Last ones first, so ripple shifts don't disturb clips still to be removed.
            foreach (var (track, clip) in targets.OrderByDescending(x => x.Clip.Start))
            {
                // The part only this clip covers (transitions on either side are shared).
                var own = clip.Length - Overlap(Previous(track, clip, null), clip) - Overlap(clip, Next(track, clip, null));
                track.Clips.Remove(clip);
                if (AutoRipple)
                    ShiftLaterClips(track, clip.Start, -own, null);
            }
        });
    }

    public void RemoveClip(string clipId) => RemoveClips([clipId]);

    /// <summary>True if the clip moves together with at least one other clip.</summary>
    public bool IsLinked(string clipId) => Project.LinkedGroup(clipId).Count > 1;

    /// <summary>Lets a clip and the clips linked to it move separately (e.g. video and its sound).</summary>
    public void Unlink(string clipId)
    {
        var group = Project.LinkedGroup(clipId);
        if (group.Count < 2)
            return;

        Edit("Unlink", () =>
        {
            foreach (var (_, clip) in group)
                clip.LinkId = null;
        });
    }

    /// <summary>Links clips (and anything already linked to them) so they move together.</summary>
    public void Link(IEnumerable<string> clipIds)
    {
        var group = clipIds.SelectMany(id => Project.LinkedGroup(id)).DistinctBy(x => x.Clip.Id).ToList();
        if (group.Count < 2)
            return;

        var link = Ids.New();
        Edit("Link", () =>
        {
            foreach (var (_, clip) in group)
                clip.LinkId = link;
        });
    }

    /// <summary>
    /// Lays a track's clips out end to end in the given order, starting where the first
    /// clip starts (used by sorting). Linked clips move along. The list must contain every
    /// clip of the track exactly once.
    /// </summary>
    public void ReorderClips(string trackId, IReadOnlyList<string> clipIdsInOrder, string description = "Reorder clips")
    {
        var track = GetTrack(trackId);
        if (clipIdsInOrder.Count != track.Clips.Count
            || clipIdsInOrder.Distinct().Count() != track.Clips.Count
            || clipIdsInOrder.Any(id => track.IndexOf(id) < 0))
            throw new ArgumentException("The new order must contain each clip of the track exactly once.", nameof(clipIdsInOrder));

        var ordered = clipIdsInOrder.Select(id => track.Clips[track.IndexOf(id)]).ToList();
        var position = track.Clips.Count > 0 ? track.Clips.Min(c => c.Start) : TimeSpan.Zero;
        var newStarts = new Dictionary<string, TimeSpan>();
        foreach (var clip in ordered)
        {
            newStarts[clip.Id] = position;
            position += clip.Length;
        }

        if (ordered.All(c => newStarts[c.Id] == c.Start))
            return; // already laid out like that: no undo step

        Edit(description, () =>
        {
            foreach (var clip in ordered)
            {
                var shift = newStarts[clip.Id] - clip.Start;
                foreach (var (other, linked) in Project.LinkedGroup(clip.Id))
                {
                    if (other != track)
                    {
                        linked.Start += shift;
                        other.SortClips();
                    }
                }

                clip.Start = newStarts[clip.Id];
            }

            track.SortClips();
        });
    }

    // ================================================================ tracks

    /// <summary>Adds a video track on top of the others.</summary>
    public Track AddVideoTrack(string? name = null)
    {
        var track = new Track { Name = name ?? $"Video {Project.VideoTracks.Count + 1}", Kind = TrackKind.Video };
        Edit("Add video track", () => Project.VideoTracks.Insert(0, track));
        return track;
    }

    public Track AddAudioTrack(string? name = null)
    {
        var track = new Track { Name = name ?? $"Audio {Project.AudioTracks.Count + 1}", Kind = TrackKind.Audio };
        Edit("Add audio track", () => Project.AudioTracks.Add(track));
        return track;
    }

    /// <summary>Removes a track and its clips. The last video track can't be removed.</summary>
    public void RemoveTrack(string trackId)
    {
        var track = GetTrack(trackId);
        if (track.Kind == TrackKind.Video && Project.VideoTracks.Count <= 1)
            throw new InvalidOperationException("A project needs at least one video track.");

        Edit($"Remove {track.Name}", () =>
        {
            if (track.Kind == TrackKind.Video)
                Project.VideoTracks.Remove(track);
            else
                Project.AudioTracks.Remove(track);
        });
    }

    /// <summary>Removes an audio track.</summary>
    public void RemoveAudioTrack(string trackId)
    {
        if (GetTrack(trackId).Kind != TrackKind.Audio)
            throw new InvalidOperationException("That's not an audio track.");
        RemoveTrack(trackId);
    }

    public void RenameTrack(string trackId, string name)
    {
        var track = GetTrack(trackId);
        Edit("Rename track", () => track.Name = name.Trim(), coalesceKey: $"rename:{trackId}");
    }

    /// <summary>Sets an audio track's gain (1.0 = 100 %), clamped to 0–400 %. Slider drags are one undo step.</summary>
    public void SetTrackVolume(string trackId, double gain)
    {
        var track = GetTrack(trackId);
        var clamped = Math.Clamp(double.IsFinite(gain) ? gain : 1.0, 0.0, 4.0);
        if (Math.Abs(track.Volume - clamped) < 1e-9)
            return; // unchanged: no undo step
        Edit($"{track.Name} volume", () => track.Volume = clamped, coalesceKey: $"volume:{trackId}");
    }

    public void SetTrackMuted(string trackId, bool muted)
    {
        var track = GetTrack(trackId);
        if (track.Muted == muted)
            return;
        var verb = track.Kind == TrackKind.Video ? (muted ? "Hide" : "Show") : (muted ? "Mute" : "Unmute");
        Edit($"{verb} {track.Name}", () => track.Muted = muted);
    }

    // ============================================================ undo / redo

    public void Undo()
    {
        if (!CanUndo)
            return;

        var (json, description) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((ProjectFile.ToJson(Project), description));
        Restore(json);
    }

    public void Redo()
    {
        if (!CanRedo)
            return;

        var (json, description) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((ProjectFile.ToJson(Project), description));
        Restore(json);
    }

    // ================================================================ helpers

    private void Edit(string description, Action change, string? coalesceKey = null)
    {
        var now = DateTime.UtcNow;
        var coalesce = coalesceKey is not null
                       && coalesceKey == _lastCoalesceKey
                       && now - _lastEditUtc < CoalesceWindow;

        var before = ProjectFile.ToJson(Project);
        change();
        ForgetLooseTransitions();

        // Safety net: never keep an edit that leaves the project inconsistent.
        if (FindProblem() is { } problem)
        {
            Project = ProjectFile.FromJson(before, FilePath is null ? null : Path.GetDirectoryName(FilePath));
            OnChanged();
            throw new InvalidOperationException(problem);
        }

        if (!coalesce)
        {
            _undo.Add((before, description));
            if (_undo.Count > MaxUndo)
                _undo.RemoveAt(0);
        }

        _redo.Clear();
        _lastCoalesceKey = coalesceKey;
        _lastEditUtc = now;
        IsDirty = true;
        OnChanged();
    }

    /// <summary>Describes what's wrong with the project, or null if it's consistent.</summary>
    private string? FindProblem()
    {
        foreach (var track in Project.AllTracks)
        {
            Clip? previous = null;
            Clip? beforePrevious = null;
            foreach (var clip in track.Clips.OrderBy(c => c.Start))
            {
                if (clip.Start < TimeSpan.Zero || clip.In < TimeSpan.Zero || clip.Out <= clip.In)
                    return $"A clip on {track.Name} has invalid timing.";
                if (clip.FadeIn < TimeSpan.Zero || clip.FadeOut < TimeSpan.Zero || clip.FadeIn > clip.Length || clip.FadeOut > clip.Length)
                    return $"A clip on {track.Name} has an invalid fade.";
                if (clip.IsText && track.Kind != TrackKind.Video)
                    return "Text can only go on a video track.";
                if (previous is not null && clip.Start < previous.End)
                {
                    // An overlap is a transition: only between two clips, each partly.
                    if (clip.Start <= previous.Start || clip.End <= previous.End)
                        return $"That would put a clip completely over another one on {track.Name}.";
                    if (clip.IsText || previous.IsText)
                        return "Text clips can't overlap other clips. Use fades for text instead.";
                    if (beforePrevious is not null && clip.Start < beforePrevious.End)
                        return $"That would make three clips overlap on {track.Name}. A transition is always between two clips.";
                }

                beforePrevious = previous;
                previous = clip;
            }
        }

        return null;
    }

    /// <summary>A clip keeps its transition style only while it overlaps the clip before it.</summary>
    private void ForgetLooseTransitions()
    {
        foreach (var track in Project.AllTracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.Transition is not null && Overlap(Previous(track, clip, null), clip) <= TimeSpan.Zero)
                    clip.Transition = null;
            }
        }
    }

    /// <summary>
    /// Moves every clip on the track that starts after <paramref name="after"/> by <paramref name="shift"/>.
    /// (Clips that come later, including one already overlapping in a transition.)
    /// </summary>
    private static void ShiftLaterClips(Track track, TimeSpan after, TimeSpan shift, HashSet<string>? except)
    {
        if (shift == TimeSpan.Zero)
            return;

        foreach (var clip in track.Clips)
        {
            if (clip.Start > after && except?.Contains(clip.Id) != true)
                clip.Start += shift;
        }

        track.SortClips();
    }

    private static IEnumerable<(TimeSpan Start, TimeSpan End)> FreeGaps(Track track, HashSet<string> except)
    {
        var position = TimeSpan.Zero;
        foreach (var clip in track.Clips.Where(c => !except.Contains(c.Id)).OrderBy(c => c.Start))
        {
            if (clip.Start > position)
                yield return (position, clip.Start);
            position = Max(position, clip.End);
        }

        yield return (position, TimeSpan.MaxValue);
    }

    private static bool IsFree(Track track, TimeSpan start, TimeSpan length, HashSet<string>? except)
    {
        if (start < TimeSpan.Zero)
            return false;
        var end = start + length;
        return !track.Clips.Any(c => except?.Contains(c.Id) != true && c.Start < end && start < c.End);
    }

    /// <summary>The clip just before this one on the track (by start), ignoring <paramref name="except"/>.</summary>
    private static Clip? Previous(Track track, Clip? clip, HashSet<string>? except) =>
        clip is null ? null : track.Clips
            .Where(c => c != clip && except?.Contains(c.Id) != true && c.Start < clip.Start)
            .MaxBy(c => c.Start);

    /// <summary>The clip just after this one on the track (by start), ignoring <paramref name="except"/>.</summary>
    private static Clip? Next(Track track, Clip? clip, HashSet<string>? except) =>
        clip is null ? null : track.Clips
            .Where(c => c != clip && except?.Contains(c.Id) != true && c.Start > clip.Start)
            .MinBy(c => c.Start);

    /// <summary>How long <paramref name="first"/> and <paramref name="second"/> overlap (their transition), or zero.</summary>
    private static TimeSpan Overlap(Clip? first, Clip? second) =>
        first is null || second is null ? TimeSpan.Zero : Max(Min(first.End, second.End) - Max(first.Start, second.Start), TimeSpan.Zero);

    private void Restore(string json)
    {
        var baseDirectory = FilePath is null ? null : Path.GetDirectoryName(FilePath);
        Project = ProjectFile.FromJson(json, baseDirectory);
        IsDirty = true;
        _lastCoalesceKey = null;
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private Track GetTrack(string trackId) =>
        Project.FindTrack(trackId) ?? throw new ArgumentException($"No track with id {trackId}.", nameof(trackId));

    private (Track Track, Clip Clip) GetClip(string clipId) =>
        Project.FindClip(clipId) ?? throw new ArgumentException($"No clip with id {clipId}.", nameof(clipId));

    private MediaItem GetMedia(string mediaId) =>
        Project.FindMedia(mediaId) ?? throw new ArgumentException($"No media with id {mediaId}.", nameof(mediaId));

    private static Track RequireKind(Track track, TrackKind kind) =>
        track.Kind == kind ? track : throw new InvalidOperationException($"{track.Name} isn't a {kind.ToString().ToLowerInvariant()} track.");

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
