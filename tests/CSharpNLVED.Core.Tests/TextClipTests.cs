using CSharpNLVED.Core.Projects;
using Xunit;
using static CSharpNLVED.Core.Tests.TestMedia;

namespace CSharpNLVED.Core.Tests;

public class TextClipTests
{
    [Fact]
    public void NewText_UsesArialBlack_AndGoesOnTheTopVideoTrack()
    {
        var editor = ProjectEditor.CreateNew();
        editor.AddVideoTrack(); // "Video 2", now on top

        var clip = editor.AddText();

        Assert.True(clip.IsText);
        Assert.Equal("Arial Black", clip.Text!.FontFamily);
        Assert.Equal(TimeSpan.Zero, clip.In);
        Assert.Equal(ProjectEditor.DefaultTextLength, clip.Length);
        Assert.Same(clip, editor.Project.VideoTracks[0].Clips.Single());
        Assert.Equal("Add text", editor.UndoDescription);
    }

    [Fact]
    public void NewText_AtAPosition_TakesTheNextFreeSpot()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));                  // 0–10 on Video 1

        var clip = editor.AddText(S(4));                    // on top of a.mp4: goes after it

        Assert.Equal(S(10), clip.Start);
    }

    [Fact]
    public void Text_CantGoOnAnAudioTrack()
    {
        var editor = ProjectEditor.CreateNew();

        Assert.Throws<InvalidOperationException>(() => editor.AddText(videoTrackId: editor.A1().Id));
        var clip = editor.AddText();
        Assert.False(editor.MoveClip(clip.Id, S(0), editor.A1().Id));
    }

    [Fact]
    public void Text_CanBeTrimmedLongerThanItWas_BothWays()
    {
        var editor = ProjectEditor.CreateNew();
        var clip = editor.AddText(S(10));                   // 10–15

        editor.TrimEdgeTo(clip.Id, ClipEdge.Right, S(40));  // no media to run out of
        editor.TrimEdgeTo(clip.Id, ClipEdge.Left, S(2));

        var text = editor.V1().Clips.Single();
        Assert.Equal(S(2), text.Start);
        Assert.Equal(S(40), text.End);
        Assert.Equal(TimeSpan.Zero, text.In);               // text never gets a media offset
    }

    [Fact]
    public void Text_LeftTrim_StopsAtZero()
    {
        var editor = ProjectEditor.CreateNew();
        var clip = editor.AddText(S(3));

        editor.TrimEdgeTo(clip.Id, ClipEdge.Left, S(-5));

        Assert.Equal(TimeSpan.Zero, editor.V1().Clips.Single().Start);
        Assert.Equal(S(8), editor.V1().Clips.Single().End);
    }

    [Fact]
    public void SplittingText_GivesTwoTextClipsWithTheirOwnStyle()
    {
        var editor = ProjectEditor.CreateNew();
        var clip = editor.AddText(style: new TextStyle { Text = "GG" });
        editor.SetFade(clip.Id, ClipEdge.Right, S(1));

        var second = editor.Split(S(2)).Single();

        var first = editor.V1().Clips[0];
        Assert.Equal(S(2), first.Length);
        Assert.Equal(S(2), second.Start);
        Assert.Equal(S(3), second.Length);
        Assert.Equal(TimeSpan.Zero, second.In);
        Assert.Equal("GG", second.Text!.Text);
        Assert.NotSame(first.Text, second.Text);            // editing one won't change the other
        Assert.Equal(S(1), second.FadeOut);
        Assert.Equal(TimeSpan.Zero, first.FadeOut);
    }

    [Fact]
    public void EditingText_WhileTyping_IsOneUndoStep()
    {
        var editor = ProjectEditor.CreateNew();
        var clip = editor.AddText();

        foreach (var typed in new[] { "N", "Ni", "Nic", "Nice" })
            editor.SetTextStyle(clip.Id, editor.V1().Clips[0].Text! with { Text = typed });

        Assert.Equal("Nice", editor.V1().Clips[0].Text!.Text);
        editor.Undo();
        Assert.Equal("Text", editor.V1().Clips[0].Text!.Text);
    }

    [Fact]
    public void TextStyle_IsKeptInRange_AndBadColoursAreRefused()
    {
        var editor = ProjectEditor.CreateNew();
        var clip = editor.AddText();

        editor.SetTextStyle(clip.Id, new TextStyle { FontSize = -3, X = 2, Y = double.NaN, FontFamily = " ", Color = "#ffcc00" });

        var style = editor.V1().Clips[0].Text!;
        Assert.Equal(4, style.FontSize);
        Assert.Equal(1, style.X);
        Assert.Equal(0.5, style.Y);
        Assert.Equal("Arial Black", style.FontFamily);
        Assert.Equal("#FFCC00", style.Color);

        var ex = Assert.Throws<InvalidOperationException>(() => editor.SetTextStyle(clip.Id, new TextStyle { Color = "red" }));
        Assert.Contains("isn't a colour", ex.Message);
    }

    [Fact]
    public void SettingTheSameStyle_AddsNoUndoStep()
    {
        var editor = ProjectEditor.CreateNew();
        var clip = editor.AddText();

        editor.SetTextStyle(clip.Id, clip.Text! with { });

        Assert.Equal("Add text", editor.UndoDescription);
    }

    [Fact]
    public void StyleCantBeSetOnAVideoClip()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));

        Assert.Throws<InvalidOperationException>(() => editor.SetTextStyle(clips[0].Id, new TextStyle()));
    }

    [Fact]
    public void TextClips_AreSavedAndLoaded_AndVideoClipsStayWithoutText()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));
        editor.AddText(style: new TextStyle { Text = "Ace!", FontSize = 120, Color = "#FF000080", X = 0.25, Y = 0.8 });

        var json = ProjectFile.ToJson(editor.Project);
        var loaded = ProjectFile.FromJson(json);

        var text = loaded.VideoTracks[0].Clips.Single(c => c.IsText).Text!;
        Assert.Equal(new TextStyle { Text = "Ace!", FontSize = 120, Color = "#FF000080", X = 0.25, Y = 0.8 }, text);
        Assert.False(loaded.VideoTracks[0].Clips.Single(c => !c.IsText).IsText);
        Assert.Single(json.Split("\"fontFamily\":")[1..]);  // only the text clip writes a style; video clips don't
    }

    // ------------------------------------------------- "Add text" at the playhead

    [Fact]
    public void AddingTextOverVideo_MakesATextTrackOnTop_InOneUndoStep()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));                   // Video 1: 0–10

        var clip = editor.AddTextOverVideo(S(3));

        Assert.Equal(2, editor.Project.VideoTracks.Count);
        Assert.Equal("Text", editor.Project.VideoTracks[0].Name);
        Assert.Same(clip, editor.Project.VideoTracks[0].Clips.Single());
        Assert.Equal(S(3), clip.Start);                     // right at the playhead, not after a.mp4

        editor.Undo();
        Assert.Single(editor.Project.VideoTracks);          // the new track goes too
    }

    [Fact]
    public void AddingTextOverVideo_UsesAFreeTrackAbove()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));
        var overlay = editor.AddVideoTrack();

        var clip = editor.AddTextOverVideo(S(3));

        Assert.Same(clip, overlay.Clips.Single());
        Assert.Equal(2, editor.Project.VideoTracks.Count);
    }

    [Fact]
    public void AddingTextOverVideo_GoesAboveAnOverlay_AndAboveOtherText()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));
        var overlay = editor.AddVideoTrack();
        editor.AddToTimeline(editor.AddMedia(Video("cam.mp4", 10, withSound: false)).Id, S(0), overlay.Id);

        var first = editor.AddTextOverVideo(S(3));          // above the overlay: new "Text" track
        var second = editor.AddTextOverVideo(S(5));         // "Text" is busy at 5 s: "Text 2"

        Assert.Equal(["Text 2", "Text", "Video 2", "Video 1"], editor.Project.VideoTracks.Select(t => t.Name));
        Assert.Same(first, editor.Project.VideoTracks[1].Clips.Single());
        Assert.Same(second, editor.Project.VideoTracks[0].Clips.Single());
    }

    [Fact]
    public void AddingTextWithNoVideoThere_UsesTheBottomTrack()
    {
        var editor = ProjectEditor.CreateNew();

        var clip = editor.AddTextOverVideo(S(2));

        Assert.Single(editor.Project.VideoTracks);
        Assert.Same(clip, editor.V1().Clips.Single());
        Assert.Equal(S(2), clip.Start);
    }

    [Fact]
    public void AddingTextOverVideo_NeedsRoomForTheWholeClip()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));
        var overlay = editor.AddVideoTrack();
        editor.AddText(S(5), overlay.Id);                   // overlay busy from 5 s

        var clip = editor.AddTextOverVideo(S(2));           // 2–7 would run into it

        Assert.Equal("Text", editor.Project.VideoTracks[0].Name);
        Assert.Same(clip, editor.Project.VideoTracks[0].Clips.Single());
    }
}
