using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CSharpNLVED.Core.Projects;

namespace CSharpNLVED.App;

/// <summary>
/// A Vegas-style timeline: a time ruler on top, one lane per track (video tracks first,
/// top to bottom, then audio tracks), clips drawn as blocks at their position.
/// It draws itself (no child controls), so it stays fast with many clips.
///
/// Mouse:
///   • click a clip to select it (its linked clips light up too); Ctrl+click adds/removes
///   • drag a clip to move it, also onto another track of the same kind; it snaps to
///     other clips' edges and the playhead (hold Alt to stop snapping)
///   • drag a clip's left/right edge to trim it
///   • drag a clip's top-left/top-right corner to set its fade in/out (the handle sits
///     at the top of the fade line, so an existing fade can be grabbed and changed)
///   • click or drag on the ruler / empty space to move the playhead
///   • wheel = zoom around the pointer, Shift+wheel (or horizontal wheel) = scroll
/// All edits go through <see cref="ProjectEditor"/>, so undo/redo works for everything.
/// </summary>
public sealed class TimelineControl : Control
{
    public const double RulerHeight = 26;
    public const double LaneGap = 4;
    public static double LaneHeight(TrackKind kind) => kind == TrackKind.Video ? 64 : 56;

    private const double LeftPad = 8;
    private const double EdgeGrab = 7;
    private const double FadeGrab = 8;          // horizontal reach of a fade handle
    private const double FadeZoneHeight = 12;   // fade handles live in the top strip of a clip
    private const double ClipInset = 3;         // clips are drawn this far inside their lane
    private const double DragThreshold = 4;
    private const double SnapPixels = 8;
    private const double MinPixelsPerSecond = 0.5;
    private const double MaxPixelsPerSecond = 800;

    private static readonly IBrush BackgroundBrush = Brush("#16181D");
    private static readonly IBrush VideoLaneBrush = Brush("#1E2128");
    private static readonly IBrush AudioLaneBrush = Brush("#1B1F22");
    private static readonly IBrush RulerBrush = Brush("#1B1D23");
    private static readonly IBrush TickBrush = Brush("#5A606B");
    private static readonly IBrush LabelBrush = Brush("#8A8F98");
    private static readonly IBrush VideoClipBrush = Brush("#3B6EA8");
    private static readonly IBrush AudioClipBrush = Brush("#3E8E6A");
    private static readonly IBrush MissingClipBrush = Brush("#8A3B3B");
    private static readonly IBrush TextClipBrush = Brush("#7B52AE");
    private static readonly IBrush ClipTextBrush = Brush("#F2F4F7");
    private static readonly IBrush ClipSubTextBrush = Brush("#C9D3E0");
    private static readonly IBrush HandleBrush = Brush("#FFFFFF");
    private static readonly IBrush AccentBrush = Brush("#FF6B4A");
    private static readonly IBrush PlayheadBrush = Brush("#FF3B3B");
    private static readonly IPen SelectedPen = new Pen(AccentBrush, 2);
    private static readonly IPen LinkedPen = new Pen(Brush("#FFB39E"), 1.5);
    private static readonly IPen ClipBorderPen = new Pen(Brush("#00000055"), 1);
    private static readonly IPen TickPen = new Pen(TickBrush, 1);
    private static readonly IPen PlayheadPen = new Pen(PlayheadBrush, 1.5);
    private static readonly IBrush DropLaneBrush = Brush("#33FF6B4A");
    private static readonly IPen DropPen = new Pen(AccentBrush, 2);
    private static readonly IPen SnapPen = new Pen(Brush("#FFD24A"), 1, new DashStyle([4, 3], 0));
    private static readonly IBrush FadeShadeBrush = Brush("#8C000000");
    private static readonly IPen FadeLinePen = new Pen(Brush("#E6FFFFFF"), 1.5);
    private static readonly IBrush FadeLabelBackBrush = Brush("#CC101216");
    private static readonly Typeface Font = new(FontFamily.Default);
    private static readonly Typeface BoldFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private static readonly double[] RulerSteps =
        [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

    private ProjectEditor? _editor;
    private double _pixelsPerSecond = 40;
    private double _scrollSeconds;
    private bool _autoFit = true;
    private TimeSpan _playhead;
    private double _lastWidth;

    private readonly HashSet<string> _selected = [];
    private string? _primary;

    private enum DragMode { None, Pending, Move, TrimLeft, TrimRight, FadeIn, FadeOut, Scrub }
    private enum Zone { None, Body, LeftEdge, RightEdge, FadeIn, FadeOut }

    private DragMode _drag;
    private string? _dragClipId;
    private double _pressX;
    private double _pressSeconds;
    private TimeSpan _originalStart;
    private TimeSpan _originalEdge;
    private double? _snapLine;

    private Cursor? _resizeCursor;
    private Cursor? _moveCursor;
    private Cursor? _fadeInCursor;
    private Cursor? _fadeOutCursor;

    public TimelineControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Ids of the clips the user selected (linked clips aren't included, but act along).</summary>
    public IReadOnlyCollection<string> SelectedClipIds => _selected;

    /// <summary>The clip clicked last, if it's still selected.</summary>
    public string? PrimaryClipId => _primary is not null && _selected.Contains(_primary) ? _primary : _selected.FirstOrDefault();

    /// <summary>The track of <see cref="PrimaryClipId"/>, or null.</summary>
    public string? SelectedTrackId =>
        PrimaryClipId is { } id ? _editor?.Project.FindClip(id)?.Track.Id : null;

    public event EventHandler? SelectionChanged;

    /// <summary>Raised with the clip id when a text clip is double-clicked (to edit its text).</summary>
    public event EventHandler<string>? TextClipDoubleClicked;

    /// <summary>Raised when zoom or scroll position changes (to sync the scrollbar).</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised with a short message when an edit was refused (e.g. clips would overlap).</summary>
    public event EventHandler<string>? EditRefused;

    public TimeSpan Playhead
    {
        get => _playhead;
        set
        {
            var max = TimeSpan.FromSeconds(TotalSeconds);
            _playhead = value < TimeSpan.Zero ? TimeSpan.Zero : value > max ? max : value;
            InvalidateVisual();
        }
    }

    public double ScrollSeconds
    {
        get => _scrollSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxScrollSeconds);
            if (Math.Abs(clamped - _scrollSeconds) < 1e-9)
                return;
            _scrollSeconds = clamped;
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double VisibleSeconds => Math.Max(0, Bounds.Width - LeftPad * 2) / _pixelsPerSecond;

    public double MaxScrollSeconds => Math.Max(0, TotalSeconds + 1 - VisibleSeconds);

    private double TotalSeconds => _editor?.Project.Duration.TotalSeconds ?? 0;

    // ------------------------------------------------------------------ API

    public void SetEditor(ProjectEditor editor)
    {
        _editor = editor;
        _autoFit = true;
        _scrollSeconds = 0;
        _playhead = TimeSpan.Zero;
        _selected.Clear();
        _primary = null;
        Refresh();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Call after the project changed.</summary>
    public void Refresh()
    {
        // Forget selected clips that no longer exist (removed, undone…).
        if (_editor is not null && _selected.RemoveWhere(id => _editor.Project.FindClip(id) is null) > 0)
            SelectionChanged?.Invoke(this, EventArgs.Empty);

        // Don't re-fit mid-drag: the zoom would shift under the mouse.
        if (_autoFit && _drag == DragMode.None)
            Fit(Bounds.Width);
        _scrollSeconds = Math.Clamp(_scrollSeconds, 0, MaxScrollSeconds);
        Playhead = _playhead; // re-clamp to the new length

        InvalidateMeasure();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectClips(IEnumerable<string> clipIds)
    {
        _selected.Clear();
        foreach (var id in clipIds)
            _selected.Add(id);
        _primary = _selected.FirstOrDefault();
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection() => SelectClips([]);

    // ---------------------------------------------------- dropping files in

    private double? _dropSeconds;
    private string? _dropTrackId;

    /// <summary>
    /// Where files dropped at <paramref name="point"/> (in this control's coordinates) would go:
    /// the time under the pointer (snapped to clip edges) and the track under it, if any.
    /// </summary>
    public (TimeSpan Time, string? TrackId) DropTargetAt(Point point)
    {
        var seconds = Snap(Math.Max(0, SecondsAt(point.X)), 0, [], enabled: true);
        return (TimeSpan.FromSeconds(seconds), LaneAt(point.Y)?.Id);
    }

    /// <summary>Shows (or with null, hides) the drop marker while files are dragged over the timeline.</summary>
    public void ShowDropMarker(Point? point)
    {
        if (point is { } p)
        {
            var (time, trackId) = DropTargetAt(p);
            _dropSeconds = time.TotalSeconds;
            _dropTrackId = trackId;
        }
        else
        {
            _dropSeconds = null;
            _dropTrackId = null;
            _snapLine = null;
        }

        InvalidateVisual();
    }

    /// <summary>Zooms so the whole project fits the width (and keeps fitting as clips are added).</summary>
    public void ZoomToFit()
    {
        _autoFit = true;
        _scrollSeconds = 0;
        Fit(Bounds.Width);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Fit(double width)
    {
        var usable = width - LeftPad * 2;
        if (usable <= 0)
            return;
        _pixelsPerSecond = Math.Clamp(usable / Math.Max(TotalSeconds, 1), MinPixelsPerSecond, MaxPixelsPerSecond);
    }

    /// <summary>Selected, or linked to a selected clip.</summary>
    private bool IsHighlighted(Clip clip, out bool directly)
    {
        directly = _selected.Contains(clip.Id);
        if (directly)
            return true;
        if (clip.LinkId is null || _editor is null)
            return false;

        foreach (var id in _selected)
        {
            if (_editor.Project.FindClip(id) is { } found && found.Clip.LinkId == clip.LinkId)
                return true;
        }

        return false;
    }

    // --------------------------------------------------------------- layout

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = RulerHeight;
        if (_editor is not null)
            height += _editor.Project.AllTracks.Sum(t => LaneHeight(t.Kind) + LaneGap);

        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_autoFit && _drag == DragMode.None)
            Fit(finalSize.Width);

        if (Math.Abs(finalSize.Width - _lastWidth) > 0.5)
        {
            _lastWidth = finalSize.Width;
            // The visible range changed: let the scrollbar know (after layout settles).
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewChanged?.Invoke(this, EventArgs.Empty));
        }

        return finalSize;
    }

    private double X(double seconds) => LeftPad + (seconds - _scrollSeconds) * _pixelsPerSecond;

    private double SecondsAt(double x) => (x - LeftPad) / _pixelsPerSecond + _scrollSeconds;

    private IEnumerable<(Track Track, double Top, double Height)> Lanes()
    {
        if (_editor is null)
            yield break;

        var y = RulerHeight;
        foreach (var track in _editor.Project.AllTracks)
        {
            var h = LaneHeight(track.Kind);
            yield return (track, y, h);
            y += h + LaneGap;
        }
    }

    private Track? LaneAt(double y) =>
        Lanes().Where(l => y >= l.Top && y < l.Top + l.Height + LaneGap).Select(l => l.Track).FirstOrDefault();

    // ---------------------------------------------------------------- render

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        if (_editor is null)
            return;

        var project = _editor.Project;
        foreach (var (track, top, height) in Lanes())
        {
            context.FillRectangle(track.Kind == TrackKind.Video ? VideoLaneBrush : AudioLaneBrush,
                new Rect(0, top, width, height));

            foreach (var clip in track.Clips)
            {
                var rect = new Rect(X(clip.Start.TotalSeconds), top + ClipInset,
                    Math.Max(3, clip.Length.TotalSeconds * _pixelsPerSecond - 1), height - ClipInset * 2);
                if (rect.Right >= 0 && rect.X <= width)
                    DrawClip(context, project, track, clip, rect);
            }
        }

        DrawRuler(context, width);

        if (_dropSeconds is { } drop)
        {
            // Files being dragged in: highlight the target lane and mark the drop time.
            var lane = Lanes().FirstOrDefault(l => l.Track.Id == _dropTrackId);
            if (lane.Track is not null)
                context.FillRectangle(DropLaneBrush, new Rect(0, lane.Top, width, lane.Height));

            var x = X(drop);
            context.DrawLine(DropPen, new Point(x, 0), new Point(x, Bounds.Height));
            context.DrawText(Text($"Drop at {Clock(TimeSpan.FromSeconds(drop), precise: true)}", 11, AccentBrush),
                new Point(x + 6, RulerHeight + 2));
        }

        if (_snapLine is { } snap)
        {
            var x = X(snap);
            context.DrawLine(SnapPen, new Point(x, RulerHeight), new Point(x, Bounds.Height));
        }

        var playheadX = X(_playhead.TotalSeconds);
        if (playheadX >= 0 && playheadX <= width)
        {
            context.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, Bounds.Height));
            context.FillRectangle(PlayheadBrush, new Rect(playheadX - 5, 0, 10, 8));
        }
    }

    private void DrawClip(DrawingContext context, Project project, Track track, Clip clip, Rect rect)
    {
        var media = clip.IsText ? null : project.FindMedia(clip.MediaId);
        var fill = clip.IsText ? TextClipBrush
            : media is null || media.IsMissing ? MissingClipBrush
            : track.Kind == TrackKind.Video ? VideoClipBrush : AudioClipBrush;
        var highlighted = IsHighlighted(clip, out var directly);

        using (context.PushOpacity(track.Muted ? 0.4 : 1))
        {
            context.DrawRectangle(fill, highlighted ? (directly ? SelectedPen : LinkedPen) : ClipBorderPen, rect, 4, 4);

            if (rect.Width >= 24)
            {
                using (context.PushClip(rect.Deflate(new Thickness(8, 2))))
                {
                    var name = clip.Text is { } style ? $"T   {TextLabel(style.Text)}" : media?.FileName ?? "(missing file)";
                    context.DrawText(Text(name, 12, ClipTextBrush, bold: true), new Point(rect.X + 9, rect.Y + 4));

                    var details = Clock(clip.Length, precise: true);
                    if (clip.Text is { } textStyle)
                        details += $"   {textStyle.FontFamily}   (double-click to edit)";
                    if (!clip.IsText && (clip.In > TimeSpan.Zero || (media is not null && clip.Out < media.Duration)))
                        details += $"   (from {Clock(clip.In, precise: true)})";
                    if (clip.LinkId is null && track.Kind == TrackKind.Video && media is { HasAudio: true })
                        details += "   unlinked";
                    if (media is { IsMissing: true })
                        details += "   ⚠ file missing";
                    context.DrawText(Text(details, 11, ClipSubTextBrush), new Point(rect.X + 9, rect.Y + 21));
                }
            }

            DrawFades(context, clip, rect);

            if (directly && rect.Width > EdgeGrab * 3)
            {
                // Trim handles.
                context.FillRectangle(HandleBrush, new Rect(rect.X + 2, rect.Y + rect.Height / 2 - 8, 3, 16));
                context.FillRectangle(HandleBrush, new Rect(rect.Right - 5, rect.Y + rect.Height / 2 - 8, 3, 16));

                // Fade handles: small squares at the top of each fade line.
                var (inX, outX) = FadeHandleXs(clip, rect.X, rect.X + clip.Length.TotalSeconds * _pixelsPerSecond);
                context.FillRectangle(HandleBrush, new Rect(Math.Max(rect.X + 1, inX - 3), rect.Y + 1, 6, 6));
                context.FillRectangle(HandleBrush, new Rect(Math.Min(rect.Right - 7, outX - 3), rect.Y + 1, 6, 6));
            }
        }
    }

    /// <summary>
    /// Shades the faded parts of a clip and draws the fade line, Vegas-style: the line goes
    /// from the bottom corner (silent/black) up to full level where the fade ends.
    /// </summary>
    private void DrawFades(DrawingContext context, Clip clip, Rect rect)
    {
        var inWidth = Math.Min(rect.Width, clip.FadeIn.TotalSeconds * _pixelsPerSecond);
        var outWidth = Math.Min(rect.Width, clip.FadeOut.TotalSeconds * _pixelsPerSecond);
        if (inWidth < 0.5 && outWidth < 0.5 && !IsDraggingFadeOf(clip))
            return;

        using (context.PushClip(rect))
        {
            if (inWidth >= 0.5)
            {
                var top = new Point(rect.X + inWidth, rect.Y);
                var bottom = new Point(rect.X, rect.Bottom);
                context.DrawGeometry(FadeShadeBrush, null, Triangle(new Point(rect.X, rect.Y), top, bottom));
                context.DrawLine(FadeLinePen, bottom, top);
            }

            if (outWidth >= 0.5)
            {
                var top = new Point(rect.Right - outWidth, rect.Y);
                var bottom = new Point(rect.Right, rect.Bottom);
                context.DrawGeometry(FadeShadeBrush, null, Triangle(new Point(rect.Right, rect.Y), top, bottom));
                context.DrawLine(FadeLinePen, top, bottom);
            }
        }

        if (IsDraggingFadeOf(clip))
        {
            // While dragging, show how long the fade is.
            var fadeIn = _drag == DragMode.FadeIn;
            var label = Text($"Fade {(fadeIn ? "in" : "out")} {(fadeIn ? clip.FadeIn : clip.FadeOut).TotalSeconds:0.00} s", 11, ClipTextBrush);
            var x = fadeIn ? rect.X + inWidth + 6 : rect.Right - outWidth - label.Width - 6;
            x = Math.Clamp(x, rect.X + 2, Math.Max(rect.X + 2, rect.Right - label.Width - 2));
            var box = new Rect(x - 3, rect.Y + FadeZoneHeight - 2, label.Width + 6, label.Height + 2);
            context.FillRectangle(FadeLabelBackBrush, box, 3);
            context.DrawText(label, new Point(x, box.Y + 1));
        }
    }

    /// <summary>The first line of a text clip's text, for its label on the timeline.</summary>
    private static string TextLabel(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.Length switch
        {
            0 => "(empty text)",
            1 => lines[0],
            _ => lines[0] + " …",
        };
    }

    private bool IsDraggingFadeOf(Clip clip) =>
        _drag is DragMode.FadeIn or DragMode.FadeOut && _dragClipId == clip.Id;

    private static StreamGeometry Triangle(Point a, Point b, Point c)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(a, true);
            ctx.LineTo(b);
            ctx.LineTo(c);
            ctx.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>X positions of the fade-in and fade-out handles (the top ends of the fade lines).</summary>
    private (double In, double Out) FadeHandleXs(Clip clip, double x0, double x1) =>
        (x0 + clip.FadeIn.TotalSeconds * _pixelsPerSecond, x1 - clip.FadeOut.TotalSeconds * _pixelsPerSecond);

    private void DrawRuler(DrawingContext context, double width)
    {
        context.FillRectangle(RulerBrush, new Rect(0, 0, width, RulerHeight));

        var step = RulerSteps.FirstOrDefault(s => s * _pixelsPerSecond >= 80, RulerSteps[^1]);
        var minor = step / 5;
        var first = Math.Floor(_scrollSeconds / step) * step;
        var last = _scrollSeconds + width / _pixelsPerSecond;

        for (var t = first; t <= last; t += minor)
        {
            var x = X(t);
            if (x < 0)
                continue;

            var isMajor = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            context.DrawLine(TickPen, new Point(x, RulerHeight - (isMajor ? 10 : 4)), new Point(x, RulerHeight));
            if (isMajor)
                context.DrawText(Text(Clock(TimeSpan.FromSeconds(t), precise: step < 1), 10, LabelBrush), new Point(x + 3, 3));
        }
    }

    private static FormattedText Text(string text, double size, IBrush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? BoldFont : Font, size, brush);

    internal static string Clock(TimeSpan t, bool precise = false)
    {
        if (t.TotalHours >= 1)
            return t.ToString(@"h\:mm\:ss");
        return precise && t.TotalSeconds < 60 && t.Milliseconds != 0
            ? t.ToString(@"m\:ss\.f")
            : t.ToString(@"m\:ss");
    }

    // ------------------------------------------------------------ hit testing

    private (Track? Track, Clip? Clip, Zone Zone) HitTest(Point p)
    {
        if (p.Y < RulerHeight)
            return (null, null, Zone.None);

        foreach (var (track, top, height) in Lanes())
        {
            if (p.Y < top || p.Y >= top + height)
                continue;

            foreach (var clip in track.Clips)
            {
                var x0 = X(clip.Start.TotalSeconds);
                var x1 = X(clip.End.TotalSeconds);
                if (p.X >= x0 && p.X < x1)
                {
                    // Top strip: fade handles win over the trim edges, like Vegas's top corners.
                    if (x1 - x0 >= EdgeGrab * 3 && p.Y - (top + ClipInset) <= FadeZoneHeight)
                    {
                        var (inX, outX) = FadeHandleXs(clip, x0, x1);
                        var toIn = Math.Abs(p.X - inX);
                        var toOut = Math.Abs(p.X - outX);
                        if (toIn <= FadeGrab && toIn <= toOut)
                            return (track, clip, Zone.FadeIn);
                        if (toOut <= FadeGrab)
                            return (track, clip, Zone.FadeOut);
                    }

                    var zone = x1 - x0 < EdgeGrab * 3 ? Zone.Body
                        : p.X - x0 <= EdgeGrab ? Zone.LeftEdge
                        : x1 - p.X <= EdgeGrab ? Zone.RightEdge
                        : Zone.Body;
                    return (track, clip, zone);
                }
            }

            return (track, null, Zone.None);
        }

        return (null, null, Zone.None);
    }

    // --------------------------------------------------------------- snapping

    /// <summary>
    /// If <paramref name="seconds"/> (or <paramref name="seconds"/> + <paramref name="length"/>)
    /// is within a few pixels of another clip's edge, the playhead or 0:00, returns the
    /// adjusted value; otherwise returns it unchanged.
    /// </summary>
    private double Snap(double seconds, double length, HashSet<string> ignore, bool enabled)
    {
        _snapLine = null;
        if (!enabled || _editor is null)
            return seconds;

        var points = new List<double> { 0, _playhead.TotalSeconds };
        foreach (var track in _editor.Project.AllTracks)
        {
            foreach (var clip in track.Clips)
            {
                if (ignore.Contains(clip.Id))
                    continue;
                points.Add(clip.Start.TotalSeconds);
                points.Add(clip.End.TotalSeconds);
            }
        }

        var threshold = SnapPixels / _pixelsPerSecond;
        double? best = null;
        foreach (var point in points)
        {
            foreach (var edge in length > 0 ? new[] { seconds, seconds + length } : new[] { seconds })
            {
                var diff = point - edge;
                if (Math.Abs(diff) <= threshold && (best is null || Math.Abs(diff) < Math.Abs(best.Value)))
                {
                    best = diff;
                    _snapLine = point;
                }
            }
        }

        return seconds + (best ?? 0);
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (_editor is null || !point.Properties.IsLeftButtonPressed)
            return;

        Focus();
        e.Handled = true;

        var p = point.Position;
        _pressX = p.X;
        _pressSeconds = SecondsAt(p.X);

        var (track, clip, zone) = HitTest(p);
        if (track is not null && clip is not null)
        {
            if (e.ClickCount == 2 && clip.IsText)
            {
                SelectClips([clip.Id]);
                TextClipDoubleClicked?.Invoke(this, clip.Id);
                return;
            }

            var addToSelection = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
            if (addToSelection)
            {
                // Ctrl+click: add or remove this clip, no dragging.
                if (!_selected.Remove(clip.Id))
                {
                    _selected.Add(clip.Id);
                    _primary = clip.Id;
                }

                InvalidateVisual();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!_selected.Contains(clip.Id))
                SelectClips([clip.Id]);
            _primary = clip.Id;
            SelectionChanged?.Invoke(this, EventArgs.Empty);

            e.Pointer.Capture(this);
            _dragClipId = clip.Id;
            _originalStart = clip.Start;
            _originalEdge = zone == Zone.LeftEdge ? clip.Start : clip.End;
            _drag = zone switch
            {
                Zone.LeftEdge => DragMode.TrimLeft,
                Zone.RightEdge => DragMode.TrimRight,
                Zone.FadeIn => DragMode.FadeIn,
                Zone.FadeOut => DragMode.FadeOut,
                _ => DragMode.Pending,
            };
        }
        else
        {
            // Ruler or empty space: move the playhead there (and keep scrubbing while dragging).
            if (track is not null && _selected.Count > 0)
                ClearSelection();
            e.Pointer.Capture(this);
            _drag = DragMode.Scrub;
            Playhead = TimeSpan.FromSeconds(Math.Max(0, SecondsAt(p.X)));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_editor is null)
            return;

        var p = e.GetPosition(this);
        var snapping = !e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        switch (_drag)
        {
            case DragMode.None:
                Cursor = HitTest(p).Zone switch
                {
                    Zone.LeftEdge or Zone.RightEdge => _resizeCursor ??= new Cursor(StandardCursorType.SizeWestEast),
                    Zone.FadeIn => _fadeInCursor ??= new Cursor(StandardCursorType.TopLeftCorner),
                    Zone.FadeOut => _fadeOutCursor ??= new Cursor(StandardCursorType.TopRightCorner),
                    _ => null,
                };
                return;

            case DragMode.Pending:
                if (Math.Abs(p.X - _pressX) <= DragThreshold && LaneAt(p.Y) == _editor.Project.FindClip(_dragClipId!)?.Track)
                    return;
                _drag = DragMode.Move;
                Cursor = _moveCursor ??= new Cursor(StandardCursorType.SizeAll);
                MoveDraggedClip(p, snapping);
                return;

            case DragMode.Move:
                MoveDraggedClip(p, snapping);
                return;

            case DragMode.TrimLeft or DragMode.TrimRight when _dragClipId is not null:
            {
                var group = _editor.Project.LinkedGroup(_dragClipId).Select(g => g.Clip.Id).ToHashSet();
                var desired = _originalEdge.TotalSeconds + (SecondsAt(p.X) - _pressSeconds);
                desired = Snap(Math.Max(0, desired), 0, group, snapping);
                var edge = _drag == DragMode.TrimLeft ? ClipEdge.Left : ClipEdge.Right;
                // Live: the editor clamps to the media and merges the whole drag into one undo step.
                TryEdit(() => _editor.TrimEdgeTo(_dragClipId, edge, TimeSpan.FromSeconds(desired)));
                InvalidateVisual();
                return;
            }

            case DragMode.FadeIn or DragMode.FadeOut when _dragClipId is not null:
            {
                if (_editor.Project.FindClip(_dragClipId) is not { } found)
                    return;
                // Snap the fade's end to other clips' edges / the playhead, then turn it into a length.
                var at = Snap(Math.Max(0, SecondsAt(p.X)), 0, [], snapping);
                var edge = _drag == DragMode.FadeIn ? ClipEdge.Left : ClipEdge.Right;
                var length = edge == ClipEdge.Left
                    ? at - found.Clip.Start.TotalSeconds
                    : found.Clip.End.TotalSeconds - at;
                // The editor clamps to the clip and merges the whole drag into one undo step.
                TryEdit(() => _editor.SetFade(_dragClipId, edge, TimeSpan.FromSeconds(Math.Max(0, length))));
                InvalidateVisual();
                return;
            }

            case DragMode.Scrub:
                Playhead = TimeSpan.FromSeconds(Math.Max(0, SecondsAt(p.X)));
                return;
        }
    }

    private void MoveDraggedClip(Point p, bool snapping)
    {
        if (_editor is null || _dragClipId is not { } clipId || _editor.Project.FindClip(clipId) is not { } found)
            return;

        var group = _editor.Project.LinkedGroup(clipId).Select(g => g.Clip.Id).ToHashSet();
        var desired = _originalStart.TotalSeconds + (SecondsAt(p.X) - _pressSeconds);
        desired = Snap(Math.Max(0, desired), found.Clip.Length.TotalSeconds, group, snapping);

        // Onto another track of the same kind if the pointer is over one.
        var lane = LaneAt(p.Y);
        var target = lane is not null && lane.Kind == found.Track.Kind ? lane.Id : found.Track.Id;
        TryEdit(() => _editor.MoveClip(clipId, TimeSpan.FromSeconds(desired), target));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        var wasEditing = _drag is DragMode.Move or DragMode.TrimLeft or DragMode.TrimRight or DragMode.FadeIn or DragMode.FadeOut;
        _drag = DragMode.None;
        _dragClipId = null;
        _snapLine = null;
        Cursor = null;

        if (wasEditing)
            Refresh(); // re-fit now that the drag is over
        else
            InvalidateVisual();
    }

    private void TryEdit(Action edit)
    {
        try
        {
            edit();
        }
        catch (InvalidOperationException ex)
        {
            EditRefused?.Invoke(this, ex.Message);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_editor is null)
            return;

        e.Handled = true;
        var p = e.GetPosition(this);

        var horizontal = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
        if (horizontal)
        {
            var amount = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            ScrollSeconds -= amount * 80 / _pixelsPerSecond;
            return;
        }

        // Zoom around the pointer, like Vegas.
        var anchor = SecondsAt(p.X);
        _pixelsPerSecond = Math.Clamp(_pixelsPerSecond * Math.Pow(1.25, e.Delta.Y), MinPixelsPerSecond, MaxPixelsPerSecond);
        _autoFit = false;
        _scrollSeconds = Math.Clamp(anchor - (p.X - LeftPad) / _pixelsPerSecond, 0, MaxScrollSeconds);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
