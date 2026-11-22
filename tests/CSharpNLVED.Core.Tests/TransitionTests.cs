using CSharpNLVED.Core.Export;
using CSharpNLVED.Core.Gpu;
using CSharpNLVED.Core.Projects;
using Xunit;
using static CSharpNLVED.Core.Tests.TestMedia;

namespace CSharpNLVED.Core.Tests;

public class TransitionTests
{
    /// <summary>a (0–10), b (10–15), c (15–23), each with its sound on Audio 1, plus a song on Music.</summary>
    private static (ProjectEditor Editor, IReadOnlyList<Clip> A, IReadOnlyList<Clip> B, IReadOnlyList<Clip> C) ThreeClips()
    {
        var editor = ProjectEditor.CreateNew();
        var a = editor.Place(Video("a.mp4", 10));
        var b = editor.Place(Video("b.mp4", 5));
        var c = editor.Place(Video("c.mp4", 8));
        editor.Place(Song("song.mp3", 60));
        return (editor, a, b, c);
    }

    private static Clip V(ProjectEditor editor, Clip clip) => editor.V1().Clips.Single(c => c.Id == clip.Id);
    private static Clip A(ProjectEditor editor, Clip clip) => editor.A1().Clips.Single(c => c.Id == clip.Id);

    [Fact]
    public void AddingATransition_OverlapsTheClips_AndTheirSound()
    {
        var (editor, a, b, _) = ThreeClips();

        editor.AddTransition(b[0].Id);

        Assert.Equal(S(9), V(editor, b[0]).Start);
        Assert.Equal(S(9), A(editor, b[1]).Start);         // the sound crossfades too
        var transition = editor.TransitionInto(b[0].Id);
        Assert.NotNull(transition);
        Assert.Equal(S(1), transition.Value.Length);
        Assert.Equal(TransitionKind.Crossfade, transition.Value.Style.Kind);
        Assert.Null(editor.TransitionInto(a[0].Id));
        Assert.Equal("Add transition", editor.UndoDescription);
    }

    [Fact]
    public void LaterClipsFollow_ButOtherTracksDont()
    {
        var (editor, _, b, c) = ThreeClips();

        editor.AddTransition(b[0].Id, S(2));

        Assert.Equal(S(13), V(editor, c[0]).Start);        // was 15: no gap after the transition
        Assert.Equal(S(13), A(editor, c[1]).Start);
        Assert.Equal(TimeSpan.Zero, editor.Music().Clips[0].Start); // the music stays put
        Assert.Equal(S(21), editor.V1().Duration);
    }

    [Fact]
    public void ChangingTheLength_IsOneUndoStep_AndUndoPutsEverythingBack()
    {
        var (editor, _, b, c) = ThreeClips();
        editor.AddTransition(b[0].Id);

        editor.SetTransitionLength(b[0].Id, S(1.5));
        editor.SetTransitionLength(b[0].Id, S(2));
        Assert.Equal(S(2), editor.TransitionInto(b[0].Id)!.Value.Length);
        Assert.Equal("Transition length", editor.UndoDescription);

        editor.Undo();                                     // the whole drag at once
        Assert.Equal(S(1), editor.TransitionInto(b[0].Id)!.Value.Length);
        editor.Undo();                                     // then the transition itself
        Assert.Equal(S(10), V(editor, b[0]).Start);
        Assert.Equal(S(15), V(editor, c[0]).Start);
    }

    [Fact]
    public void RemovingATransition_PutsTheClipsBackEndToEnd()
    {
        var (editor, _, b, c) = ThreeClips();
        editor.AddTransition(b[0].Id, S(2));
        editor.SetTransitionStyle(b[0].Id, new Transition { Kind = TransitionKind.Crossfade });

        editor.RemoveTransition(b[0].Id);

        Assert.Equal(S(10), V(editor, b[0]).Start);
        Assert.Equal(S(15), V(editor, c[0]).Start);
        Assert.Null(editor.TransitionInto(b[0].Id));
        Assert.Null(V(editor, b[0]).Transition);           // the style goes with the overlap
        Assert.Equal("Remove transition", editor.UndoDescription);
    }

    [Fact]
    public void Length_IsLimitedByTheShorterClip()
    {
        var (editor, _, b, _) = ThreeClips();              // b is 5 s

        editor.AddTransition(b[0].Id, S(30));

        var frame = editor.Project.Settings.FrameDuration;
        Assert.Equal(S(5) - frame, editor.TransitionInto(b[0].Id)!.Value.Length);
    }

    [Fact]
    public void TransitionsOnBothEnds_CantCross()
    {
        var (editor, _, b, c) = ThreeClips();              // b is 5 s
        editor.AddTransition(b[0].Id, S(3));

        editor.AddTransition(c[0].Id, S(4));               // only 2 s of b are left

        var frame = editor.Project.Settings.FrameDuration;
        Assert.Equal(S(2) - frame, editor.TransitionInto(c[0].Id)!.Value.Length);
    }

    [Fact]
    public void ATransition_NeedsTwoClipsThatTouch()
    {
        var editor = ProjectEditor.CreateNew();
        var a = editor.Place(Video("a.mp4", 10));
        var b = editor.Place(Video("b.mp4", 5), at: 12);   // a 2 s gap

        var gap = Assert.Throws<InvalidOperationException>(() => editor.AddTransition(b[0].Id));
        var first = Assert.Throws<InvalidOperationException>(() => editor.AddTransition(a[0].Id));

        Assert.Contains("gap", gap.Message);
        Assert.Contains("no clip before", first.Message);
    }

    [Fact]
    public void TextClips_CantHaveTransitions()
    {
        var editor = ProjectEditor.CreateNew();
        editor.AddText(S(0));
        var second = editor.AddText(S(5));

        var ex = Assert.Throws<InvalidOperationException>(() => editor.AddTransition(second.Id));

        Assert.Contains("Text clips", ex.Message);
    }

    [Fact]
    public void Style_NeedsATransition_AndIsSavedAndLoaded()
    {
        var (editor, _, b, c) = ThreeClips();
        Assert.Throws<InvalidOperationException>(() => editor.SetTransitionStyle(c[0].Id, new Transition()));

        editor.AddTransition(b[0].Id);
        editor.SetTransitionStyle(b[0].Id, new Transition { Kind = TransitionKind.Crossfade }); // the default: nothing to store

        var json = ProjectFile.ToJson(editor.Project);
        var loaded = ProjectFile.FromJson(json);
        var clip = loaded.VideoTracks[0].Clips.Single(x => x.Id == b[0].Id);
        Assert.Null(clip.Transition);                      // null = crossfade
        Assert.Equal(S(9), clip.Start);                    // the overlap is the transition
        Assert.Equal(3, loaded.FormatVersion);
    }

    [Fact]
    public void MovingAClipAway_EndsItsTransition()
    {
        var (editor, _, b, _) = ThreeClips();
        editor.AddTransition(b[0].Id);
        editor.SetTransitionStyle(b[0].Id, new Transition());

        editor.MoveClip(b[0].Id, S(40));

        Assert.Null(editor.TransitionInto(b[0].Id));
        Assert.Null(V(editor, b[0]).Transition);
    }

    [Fact]
    public void Splitting_InsideATransition_DoesNothing_ButOutsideWorks()
    {
        var (editor, _, b, _) = ThreeClips();
        editor.AddTransition(b[0].Id);                     // a 0–10 and b 9–14 overlap 9–10

        var inside = editor.Split(S(9.5));                 // only the music (also under the playhead) is cut
        Assert.All(inside, c => Assert.Contains(c, editor.Music().Clips));
        Assert.Equal(2, editor.Split(S(5)).Count(c => !editor.Music().Clips.Contains(c))); // a's picture and sound
    }

    [Fact]
    public void TrimmingTheLaterClipsStart_ShortensTheTransition()
    {
        var (editor, _, b, _) = ThreeClips();
        editor.AddTransition(b[0].Id);                     // 9–10

        editor.TrimEdgeTo(b[0].Id, ClipEdge.Left, S(9.5));

        Assert.Equal(S(0.5), editor.TransitionInto(b[0].Id)!.Value.Length);
    }

    [Fact]
    public void TrimmingTheLaterClipsEnd_CantPutItInsideTheEarlierOne()
    {
        var (editor, a, b, _) = ThreeClips();
        editor.AddTransition(b[0].Id);                     // b: 9–14

        editor.TrimEdgeTo(b[0].Id, ClipEdge.Right, S(9.2));

        var frame = editor.Project.Settings.FrameDuration;
        Assert.Equal(V(editor, a[0]).End + frame, V(editor, b[0]).End);
    }

    [Fact]
    public void RippleTrim_KeepsTheTransition()
    {
        var (editor, a, b, c) = ThreeClips();
        editor.AddTransition(b[0].Id);                     // a 0–10, b 9–14, c 14–22
        editor.AutoRipple = true;

        editor.TrimEdgeTo(a[0].Id, ClipEdge.Right, S(8)); // a ends 2 s earlier

        Assert.Equal(S(7), V(editor, b[0]).Start);
        Assert.Equal(S(1), editor.TransitionInto(b[0].Id)!.Value.Length);
        Assert.Equal(S(12), V(editor, c[0]).Start);
    }

    [Fact]
    public void RippleDelete_OfAClipBetweenTwoTransitions_JoinsItsNeighbours()
    {
        var (editor, a, b, c) = ThreeClips();
        editor.AddTransition(b[0].Id);                     // a 0–10, b 9–14
        editor.AddTransition(c[0].Id);                     // c 13–21
        editor.AutoRipple = true;

        editor.RemoveClip(b[0].Id);

        Assert.Equal(V(editor, a[0]).End, V(editor, c[0]).Start); // they meet: no gap, no overlap
        Assert.Null(editor.TransitionInto(c[0].Id));
    }

    [Fact]
    public void Export_ExplainsTransitionsArentSupportedYet()
    {
        var (editor, _, b, _) = ThreeClips();
        editor.AddTransition(b[0].Id);
        var nvenc = new EncoderTestResult(new HardwareEncoder("hevc_nvenc", GpuApi.Nvenc, VideoCodec.Hevc), true, null, null, null);

        var ex = Assert.Throws<ExportException>(() =>
            ExportPlanner.Build(editor.Project, nvenc, ExportQuality.Balanced, Path.Combine(Path.GetTempPath(), "out.mp4")));

        Assert.Contains("transitions", ex.Message);
    }
}
