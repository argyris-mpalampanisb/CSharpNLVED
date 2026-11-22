using System.Globalization;
using CSharpNLVED.Core.Export;
using CSharpNLVED.Core.Gpu;
using CSharpNLVED.Core.Projects;
using Xunit;
using static CSharpNLVED.Core.Tests.TestMedia;

namespace CSharpNLVED.Core.Tests;

public class ExportPlannerTests
{
    private static readonly EncoderTestResult Nvenc =
        new(new HardwareEncoder("hevc_nvenc", GpuApi.Nvenc, VideoCodec.Hevc), true, null, null, null);

    private static readonly EncoderTestResult Vaapi =
        new(new HardwareEncoder("h264_vaapi", GpuApi.Vaapi, VideoCodec.H264), true, null, null, "/dev/dri/renderD128");

    private static string Out => Path.Combine(Path.GetTempPath(), "out.mp4");

    /// <summary>a.mp4 trimmed to 1.5–10 s, then b.mp4 (no sound), plus music at 30 %.</summary>
    private static ProjectEditor TwoClipsAndMusic()
    {
        var editor = ProjectEditor.CreateNew();
        var a = editor.Place(Video("a.mp4", 10));
        editor.TrimEdgeTo(a[0].Id, ClipEdge.Left, S(1.5));
        editor.MoveClip(a[0].Id, S(0));
        editor.Place(Video("b.mp4", 5, withSound: false));
        editor.Place(Song("song.mp3", 60));
        editor.SetTrackVolume(editor.Music().Id, 0.3);
        editor.TrimEdgeTo(editor.Music().Clips[0].Id, ClipEdge.Right, S(13.5));
        return editor;
    }

    private static string FilterGraph(ExportPlan plan) =>
        plan.Arguments[plan.Arguments.ToList().IndexOf("-filter_complex") + 1];

    [Fact]
    public void Builds_OneInputPerClip_WithTrimPoints()
    {
        var plan = ExportPlanner.Build(TwoClipsAndMusic().Project, Nvenc, ExportQuality.Balanced, Out);

        var args = plan.Arguments.ToList();
        var firstInput = args.IndexOf("-i");
        Assert.Equal("-ss", args[firstInput - 5]);
        Assert.Equal("1.5", args[firstInput - 4]);   // dot, not comma, whatever the language
        Assert.Equal("8.5", args[firstInput - 2]);   // -t = clip length
        Assert.Equal("-an", args[firstInput - 1]);   // video input: picture only
        Assert.Equal(4, args.Count(a => a == "-i")); // a video, a sound, b video, song
        Assert.Equal(S(13.5), plan.Duration);
    }

    [Fact]
    public void LaysOutTracks_MixesAudio_AndAppliesVolume()
    {
        var graph = FilterGraph(ExportPlanner.Build(TwoClipsAndMusic().Project, Nvenc, ExportQuality.Balanced, Out));

        Assert.Contains("concat=n=2:v=1:a=0", graph);              // a + b on the video track
        Assert.Contains("volume=0.3", graph);                      // music track volume
        Assert.Contains("amix=inputs=2:duration=longest:normalize=0", graph);
        Assert.Contains("format=nv12[vout]", graph);
    }

    [Fact]
    public void Gaps_BecomeBlackOrSilence()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 5), at: 3);

        var graph = FilterGraph(ExportPlanner.Build(editor.Project, Nvenc, ExportQuality.Balanced, Out));

        Assert.Contains("color=c=black:s=1920x1080:r=30:d=3", graph);
        Assert.Contains("anullsrc=", graph);
    }

    [Fact]
    public void UpperVideoTracks_AreOverlaidWithTransparency()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));
        var overlay = editor.AddVideoTrack();
        editor.AddToTimeline(editor.AddMedia(Video("logo.mp4", 4, withSound: false)).Id, S(2), overlay.Id);

        var graph = FilterGraph(ExportPlanner.Build(editor.Project, Nvenc, ExportQuality.Balanced, Out));

        Assert.Contains("format=yuva420p", graph);
        Assert.Contains("color=c=black@0", graph);
        Assert.Contains("overlay=format=auto", graph);
    }

    [Fact]
    public void Fades_AreApplied()
    {
        var editor = ProjectEditor.CreateNew();
        var clips = editor.Place(Video("a.mp4", 10));
        foreach (var clip in clips)
        {
            clip.FadeIn = S(1);
            clip.FadeOut = S(2);
        }

        var graph = FilterGraph(ExportPlanner.Build(editor.Project, Nvenc, ExportQuality.Balanced, Out));

        Assert.Contains("fade=t=in:st=0:d=1", graph);
        Assert.Contains("fade=t=out:st=8:d=2", graph);
        Assert.Contains("afade=t=in:st=0:d=1", graph);
        Assert.Contains("afade=t=out:st=8:d=2", graph);
    }

    [Fact]
    public void UsesTheGpuEncoder_WithAppleFriendlyHevcTag()
    {
        var args = ExportPlanner.Build(TwoClipsAndMusic().Project, Nvenc, ExportQuality.High, Out).Arguments.ToList();

        Assert.Equal("hevc_nvenc", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("19", args[args.IndexOf("-cq") + 1]);
        Assert.Equal("hvc1", args[args.IndexOf("-tag:v") + 1]);
        Assert.Contains("+faststart", args);
    }

    [Fact]
    public void Vaapi_UploadsFramesToTheGpu()
    {
        var plan = ExportPlanner.Build(TwoClipsAndMusic().Project, Vaapi, ExportQuality.Balanced, Out);
        var args = plan.Arguments.ToList();

        Assert.Equal("/dev/dri/renderD128", args[args.IndexOf("-vaapi_device") + 1]);
        Assert.Contains("format=nv12,hwupload[vout]", FilterGraph(plan));
    }

    [Fact]
    public void MutedTracks_AreLeftOut()
    {
        var editor = TwoClipsAndMusic();
        editor.SetTrackMuted(editor.Music().Id, true);

        var graph = FilterGraph(ExportPlanner.Build(editor.Project, Nvenc, ExportQuality.Balanced, Out));

        Assert.DoesNotContain("amix", graph);
    }

    [Fact]
    public void WritesToATempFileFirst()
    {
        var plan = ExportPlanner.Build(TwoClipsAndMusic().Project, Nvenc, ExportQuality.Balanced, Out);

        Assert.EndsWith("out.csnlved-tmp.mp4", plan.TempOutputPath);
        Assert.Equal(plan.TempOutputPath, plan.Arguments[^1]);
    }

    [Fact]
    public void EmptyProject_IsRefused()
    {
        Assert.Throws<ExportException>(() =>
            ExportPlanner.Build(ProjectEditor.CreateNew().Project, Nvenc, ExportQuality.Balanced, Out));
    }

    [Fact]
    public void Seconds_IgnoreTheComputersLanguage()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ro-RO");
            Assert.Equal("2.25", ExportPlanner.Seconds(TimeSpan.FromSeconds(2.25)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
