using CSharpNLVED.Core.Projects;
using Xunit;
using static CSharpNLVED.Core.Tests.TestMedia;

namespace CSharpNLVED.Core.Tests;

public class ProjectEditorTests
{
    [Fact]
    public void VideoWithSound_BecomesALinkedVideoAndAudioClip()
    {
        var editor = ProjectEditor.CreateNew();

        var clips = editor.Place(Video("a.mp4", 10));

        Assert.Equal(2, clips.Count);
        Assert.Single(editor.V1().Clips);
        Assert.Single(editor.A1().Clips);
        Assert.NotNull(clips[0].LinkId);
        Assert.Equal(clips[0].LinkId, clips[1].LinkId);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void Clips_AreAddedAfterTheLastOne_AndMusicGoesToTheMusicTrack()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));
        var second = editor.Place(Video("b.mp4", 5));
        var song = editor.Place(Song("song.mp3", 60));

        Assert.Equal(S(10), second[0].Start);
        Assert.Equal(S(15), editor.V1().Duration);
        Assert.Single(editor.Music().Clips);
        Assert.Equal(TimeSpan.Zero, song[0].Start);
        Assert.Equal(S(60), editor.Project.Duration); // music is the longest
    }

    [Fact]
    public void DroppingAtAPosition_UsesIt_OrTheNextFreeSpace()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10), at: 20);       // free spot: lands exactly there
        var b = editor.Place(Video("b.mp4", 5), at: 22); // on top of a.mp4: next free space after it
        var c = editor.Place(Video("c.mp4", 5), at: 3);  // fits before a.mp4

        Assert.Equal(S(20), editor.V1().Clips.Single(x => x.MediaId == editor.Project.Media[0].Id).Start);
        Assert.Equal(S(30), b[0].Start);
        Assert.Equal(S(3), c[0].Start);
        Assert.Equal(S(30), b[1].Start); // its sound lands with it
    }

    [Fact]
    public void Moving_ALinkedClip_MovesItsPartner()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));

        editor.MoveClip(clips[0].Id, S(4));

        Assert.Equal(S(4), editor.V1().Clips[0].Start);
        Assert.Equal(S(4), editor.A1().Clips[0].Start);
    }

    [Fact]
    public void Moving_OntoAnotherClip_LandsOnTheNearestFreeSpot()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));            // 0–10
        var b = editor.Place(Video("b.mp4", 5));     // 10–15

        editor.MoveClip(b[0].Id, S(2));              // would overlap a.mp4

        Assert.Equal(S(10), editor.V1().Clips[1].Start); // stays right after it
    }

    [Fact]
    public void Clips_CanMoveToAnotherTrackOfTheSameKind()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));
        var overlay = editor.AddVideoTrack();

        editor.MoveClip(clips[0].Id, S(0), overlay.Id);

        Assert.Single(editor.Project.FindTrack(overlay.Id)!.Clips);
        Assert.Empty(editor.Project.VideoTracks[1].Clips);
        Assert.Equal(overlay.Id, editor.Project.VideoTracks[0].Id); // new video tracks go on top
    }

    [Fact]
    public void Unlinked_Clips_MoveSeparately()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));

        editor.Unlink(clips[0].Id);
        editor.MoveClip(clips[1].Id, S(3));

        Assert.Equal(TimeSpan.Zero, editor.V1().Clips[0].Start);
        Assert.Equal(S(3), editor.A1().Clips[0].Start);
        Assert.False(editor.IsLinked(clips[0].Id));
    }

    [Fact]
    public void Split_CutsLinkedClipsTogether_AndKeepsTheHalvesLinked()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));

        var created = editor.Split(S(4));

        Assert.Equal(2, created.Count);
        Assert.Equal(2, editor.V1().Clips.Count);
        Assert.Equal(2, editor.A1().Clips.Count);
        Assert.Equal((TimeSpan.Zero, S(4)), (editor.V1().Clips[0].In, editor.V1().Clips[0].Out));
        Assert.Equal((S(4), S(10)), (editor.V1().Clips[1].In, editor.V1().Clips[1].Out));
        Assert.Equal(S(4), editor.V1().Clips[1].Start);
        Assert.Equal(created[0].LinkId, created[1].LinkId);
        Assert.NotEqual(editor.V1().Clips[0].LinkId, editor.V1().Clips[1].LinkId);
    }

    [Fact]
    public void Split_AtTheVeryEdge_DoesNothing()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));

        Assert.Empty(editor.Split(TimeSpan.Zero));
        Assert.Empty(editor.Split(S(10)));
    }

    [Fact]
    public void TrimmingTheEnd_TrimsLinkedClips_AndLeavesAGap()
    {
        var editor = ProjectEditor.CreateNew();
        var a = editor.Place(Video("a.mp4", 10));
        editor.Place(Video("b.mp4", 5));

        editor.TrimEdgeTo(a[0].Id, ClipEdge.Right, S(6));

        Assert.Equal(S(6), editor.V1().Clips[0].Out);
        Assert.Equal(S(6), editor.A1().Clips[0].Out);
        Assert.Equal(S(10), editor.V1().Clips[1].Start); // without ripple, b.mp4 stays put
    }

    [Fact]
    public void TrimmingTheEnd_WithAutoRipple_PullsLaterClipsIn()
    {
        var editor = ProjectEditor.CreateNew();
        editor.AutoRipple = true;
        var a = editor.Place(Video("a.mp4", 10));
        editor.Place(Video("b.mp4", 5));

        editor.TrimEdgeTo(a[0].Id, ClipEdge.Right, S(6));

        Assert.Equal(S(6), editor.V1().Clips[1].Start);
        Assert.Equal(S(6), editor.A1().Clips[1].Start);
    }

    [Fact]
    public void TrimmingTheStart_KeepsTheEndInPlace()
    {
        var editor = ProjectEditor.CreateNew();
        var a = editor.Place(Video("a.mp4", 10));

        editor.TrimEdgeTo(a[0].Id, ClipEdge.Left, S(3));

        var clip = editor.V1().Clips[0];
        Assert.Equal(S(3), clip.Start);
        Assert.Equal(S(3), clip.In);
        Assert.Equal(S(10), clip.End);
    }

    [Fact]
    public void Trimming_IsLimitedByTheMediaAndTheNextClip()
    {
        var editor = ProjectEditor.CreateNew();
        var a = editor.Place(Video("a.mp4", 10), at: 0);
        editor.Place(Video("b.mp4", 5), at: 12);

        editor.TrimEdgeTo(a[0].Id, ClipEdge.Right, S(30));   // media is only 10 s
        Assert.Equal(S(10), editor.V1().Clips[0].End);

        editor.TrimEdgeTo(a[0].Id, ClipEdge.Left, S(-5));    // can't start before the media
        Assert.Equal(TimeSpan.Zero, editor.V1().Clips[0].In);

        editor.TrimEdgeTo(a[0].Id, ClipEdge.Right, S(2));
        editor.TrimEdgeTo(a[0].Id, ClipEdge.Left, S(5));     // past the end: keeps one frame
        Assert.Equal(editor.Project.Settings.FrameDuration, editor.V1().Clips[0].Length);
    }

    [Fact]
    public void Removing_RemovesLinkedClipsToo_AndAutoRippleClosesTheGap()
    {
        var editor = ProjectEditor.CreateNew();
        editor.AutoRipple = true;
        var a = editor.Place(Video("a.mp4", 10));
        editor.Place(Video("b.mp4", 5));

        editor.RemoveClip(a[0].Id);

        Assert.Single(editor.V1().Clips);
        Assert.Single(editor.A1().Clips);
        Assert.Equal(TimeSpan.Zero, editor.V1().Clips[0].Start);
        Assert.Equal(TimeSpan.Zero, editor.A1().Clips[0].Start);
    }

    [Fact]
    public void Removing_WithoutAutoRipple_LeavesAGap()
    {
        var editor = ProjectEditor.CreateNew();
        var a = editor.Place(Video("a.mp4", 10));
        editor.Place(Video("b.mp4", 5));

        editor.RemoveClip(a[0].Id);

        Assert.Equal(S(10), editor.V1().Clips[0].Start);
    }

    [Fact]
    public void UndoAndRedo_RestoreTheProject()
    {
        var editor = ProjectEditor.CreateNew();
        var a = editor.Place(Video("a.mp4", 10));
        editor.MoveClip(a[0].Id, S(5));

        editor.Undo();
        Assert.Equal(TimeSpan.Zero, editor.V1().Clips[0].Start);

        editor.Redo();
        Assert.Equal(S(5), editor.V1().Clips[0].Start);
        Assert.Equal("Move clip", editor.UndoDescription);
    }

    [Fact]
    public void DraggingTheVolumeSlider_IsOneUndoStep()
    {
        var editor = ProjectEditor.CreateNew();
        var track = editor.A1().Id;

        // 100 % down to 50 % in 5 % steps (integer steps: adding 0.05 repeatedly drifts).
        for (var step = 20; step >= 10; step--)
            editor.SetTrackVolume(track, step * 0.05);

        Assert.InRange(editor.A1().Volume, 0.49, 0.51);
        editor.Undo();
        Assert.Equal(1.0, editor.A1().Volume);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void LastVideoTrack_CantBeRemoved()
    {
        var editor = ProjectEditor.CreateNew();
        Assert.Throws<InvalidOperationException>(() => editor.RemoveTrack(editor.V1().Id));

        var overlay = editor.AddVideoTrack();
        editor.RemoveTrack(overlay.Id);
        Assert.Single(editor.Project.VideoTracks);
    }

    [Fact]
    public void FirstVideoClip_SetsTheExportSize()
    {
        var editor = ProjectEditor.CreateNew();
        var phone = Video("phone.mp4", 3);
        phone.Width = 1081;
        phone.Height = 1920;
        phone.FrameRate = new Rational(30000, 1001);

        editor.Place(phone);

        Assert.Equal(1080, editor.Project.Settings.Width);  // rounded down to even
        Assert.Equal(1920, editor.Project.Settings.Height);
        Assert.Equal(new Rational(30000, 1001), editor.Project.Settings.FrameRate);
    }

    [Fact]
    public void SortingByName_LaysClipsOutInOrder_WithTheirSound()
    {
        var editor = ProjectEditor.CreateNew();
        var c = editor.Place(Video("c.mp4", 2));
        var a = editor.Place(Video("a.mp4", 3));
        var b = editor.Place(Video("b.mp4", 4));

        editor.ReorderClips(editor.V1().Id, [a[0].Id, b[0].Id, c[0].Id], "Sort by name");

        Assert.Equal(new[] { a[0].Id, b[0].Id, c[0].Id }, editor.V1().Clips.Select(x => x.Id));
        Assert.Equal(new[] { S(0), S(3), S(7) }, editor.V1().Clips.Select(x => x.Start));
        Assert.Equal(new[] { a[1].Id, b[1].Id, c[1].Id }, editor.A1().Clips.Select(x => x.Id));

        editor.Undo();
        Assert.Equal(c[0].Id, editor.V1().Clips[0].Id);
    }

    [Theory]
    [InlineData("clip2.mp4", "clip10.mp4")]
    [InlineData("GX010023.MP4", "GX010024.MP4")]
    [InlineData("a.mp4", "B.mp4")]
    [InlineData("video 9", "video 09b")]
    public void NaturalSort_PutsNamesInHumanOrder(string first, string second)
    {
        Assert.True(NaturalStringComparer.Instance.Compare(first, second) < 0);
        Assert.True(NaturalStringComparer.Instance.Compare(second, first) > 0);
    }
}
