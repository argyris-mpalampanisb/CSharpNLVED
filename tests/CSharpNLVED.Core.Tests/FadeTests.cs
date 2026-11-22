using CSharpNLVED.Core.Projects;
using Xunit;
using static CSharpNLVED.Core.Tests.TestMedia;

namespace CSharpNLVED.Core.Tests;

public class FadeTests
{
    [Fact]
    public void FadeIn_IsSetOnTheClipAndItsLinkedSound()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));

        editor.SetFade(clips[0].Id, ClipEdge.Left, S(1.5));

        Assert.Equal(S(1.5), editor.V1().Clips[0].FadeIn);
        Assert.Equal(S(1.5), editor.A1().Clips[0].FadeIn);
        Assert.Equal(TimeSpan.Zero, editor.V1().Clips[0].FadeOut);
    }

    [Fact]
    public void Fades_OnUnlinkedClips_AreIndependent()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));
        editor.Unlink(clips[0].Id);

        editor.SetFade(clips[1].Id, ClipEdge.Right, S(2));

        Assert.Equal(TimeSpan.Zero, editor.V1().Clips[0].FadeOut);
        Assert.Equal(S(2), editor.A1().Clips[0].FadeOut);
    }

    [Fact]
    public void Fade_IsLimitedToTheClip_AndToTheRoomLeftByTheOtherFade()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));

        editor.SetFade(clips[0].Id, ClipEdge.Left, S(-3));
        Assert.Equal(TimeSpan.Zero, editor.V1().Clips[0].FadeIn);

        editor.SetFade(clips[0].Id, ClipEdge.Right, S(4));
        editor.SetFade(clips[0].Id, ClipEdge.Left, S(50));

        Assert.Equal(S(6), editor.V1().Clips[0].FadeIn);
        Assert.Equal(S(4), editor.V1().Clips[0].FadeOut);
    }

    [Fact]
    public void DraggingAFade_IsOneUndoStep()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));

        editor.SetFade(clips[0].Id, ClipEdge.Left, S(0.5));
        editor.SetFade(clips[0].Id, ClipEdge.Left, S(1));
        editor.SetFade(clips[0].Id, ClipEdge.Left, S(2));
        Assert.Equal("Fade in", editor.UndoDescription);

        editor.Undo();

        Assert.Equal(TimeSpan.Zero, editor.V1().Clips[0].FadeIn);
        Assert.Equal(TimeSpan.Zero, editor.A1().Clips[0].FadeIn);
    }

    [Fact]
    public void SettingTheSameFade_AddsNoUndoStep()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));

        editor.SetFade(clips[0].Id, ClipEdge.Left, TimeSpan.Zero);

        Assert.NotEqual("Fade in", editor.UndoDescription);
    }

    [Fact]
    public void TrimmingShorterThanAFade_ShortensTheFade()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));
        editor.SetFade(clips[0].Id, ClipEdge.Left, S(8));

        editor.TrimEdgeTo(clips[0].Id, ClipEdge.Left, S(5)); // clip is now 5 s long

        Assert.Equal(S(5), editor.V1().Clips[0].FadeIn);
    }

    [Fact]
    public void Fades_AreSavedAndLoaded()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));
        editor.SetFade(clips[0].Id, ClipEdge.Left, S(1.25));
        editor.SetFade(clips[0].Id, ClipEdge.Right, S(2.5));

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(editor.Project), null);

        Assert.Equal(S(1.25), loaded.VideoTracks[0].Clips[0].FadeIn);
        Assert.Equal(S(2.5), loaded.VideoTracks[0].Clips[0].FadeOut);
    }
}
