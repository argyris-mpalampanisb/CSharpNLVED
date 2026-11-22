using System.Globalization;
using CSharpNLVED.Core.Export;
using CSharpNLVED.Core.Gpu;
using CSharpNLVED.Core.Projects;
using CSharpNLVED.Core.Text;
using Xunit;
using static CSharpNLVED.Core.Tests.TestMedia;

namespace CSharpNLVED.Core.Tests;

public class TextExportTests
{
    private static readonly EncoderTestResult Nvenc =
        new(new HardwareEncoder("hevc_nvenc", GpuApi.Nvenc, VideoCodec.Hevc), true, null, null, null);

    private static string Out => Path.Combine(Path.GetTempPath(), "out.mp4");

    /// <summary>Pretends every font is installed as "/fonts/&lt;name&gt;.ttf".</summary>
    private static string? FakeFonts(string family) => $"/fonts/{family}.ttf";

    private static ExportPlan Plan(ProjectEditor editor, Func<string, string?>? fonts = null) =>
        ExportPlanner.Build(editor.Project, Nvenc, ExportQuality.Balanced, Out, fonts ?? FakeFonts);

    private static string FilterGraph(ExportPlan plan) =>
        plan.Arguments[plan.Arguments.ToList().IndexOf("-filter_complex") + 1];

    [Fact]
    public void Text_IsDrawnWithDrawtext_FromAWorkFile()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));
        editor.AddText(S(2), editor.AddVideoTrack().Id, new TextStyle { Text = "It's 100%: \"GG\"" });

        var plan = Plan(editor);
        var graph = FilterGraph(plan);

        Assert.Contains("drawtext=fontfile=font0.ttf:textfile=text0.txt:expansion=none:", graph);
        Assert.Contains("enable='gte(t,2)*lt(t,7)'", graph);
        Assert.DoesNotContain("GG", graph); // the text itself never goes into the command
        Assert.Equal("It's 100%: \"GG\"", plan.Files.Single(f => f.Name == "text0.txt").Text);
        Assert.Equal("/fonts/Arial Black.ttf", plan.Files.Single(f => f.Name == "font0.ttf").CopyFrom);
    }

    [Fact]
    public void TextSizes_ScaleWithTheExportHeight_AndColoursUseFFmpegSyntax()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Project.Settings.Width = 2560;
        editor.Project.Settings.Height = 1600;
        editor.AddText(style: new TextStyle { FontSize = 96, OutlineWidth = 4, Color = "#FFCC00", OutlineColor = "#00000080", X = 0.25, Y = 0.8 });

        var graph = FilterGraph(Plan(editor));

        Assert.Contains("fontsize=142", graph);              // 96 × 1600/1080
        Assert.Contains("borderw=6", graph);                 // 4 × 1600/1080, rounded
        Assert.Contains("fontcolor=0xFFCC00", graph);
        Assert.Contains("bordercolor=0x00000080", graph);
        Assert.Contains("x=(w-text_w)*0.25:y=(h-text_h)*0.8", graph);
        Assert.Contains("text_align=L", graph);              // placed on the left: lines line up on the left
    }

    [Theory]
    [InlineData(0.05, "L")]
    [InlineData(0.5, "C")]
    [InlineData(0.95, "R")]
    public void LinesLineUp_LikeTheTextIsPlaced(double x, string align)
    {
        var editor = ProjectEditor.CreateNew();
        editor.AddText(style: new TextStyle { X = x });

        Assert.Contains($"text_align={align}:", FilterGraph(Plan(editor)));
    }

    [Fact]
    public void NoOutline_MeansNoBorderOptions()
    {
        var editor = ProjectEditor.CreateNew();
        editor.AddText(style: new TextStyle { OutlineWidth = 0 });

        Assert.DoesNotContain("borderw", FilterGraph(Plan(editor)));
    }

    [Fact]
    public void Fades_BecomeAnOpacityRamp()
    {
        var editor = ProjectEditor.CreateNew();
        var clip = editor.AddText(S(10));                    // 10–15
        editor.SetFade(clip.Id, ClipEdge.Left, S(1));
        editor.SetFade(clip.Id, ClipEdge.Right, S(2));

        Assert.Contains("alpha='clip(min((t-10)/1,(15-t)/2),0,1)'", FilterGraph(Plan(editor)));
    }

    [Fact]
    public void FadeInOnly_HasASingleRamp()
    {
        var editor = ProjectEditor.CreateNew();
        var clip = editor.AddText();
        editor.SetFade(clip.Id, ClipEdge.Left, S(0.5));

        Assert.Contains("alpha='clip((t-0)/0.5,0,1)'", FilterGraph(Plan(editor)));
    }

    [Fact]
    public void TextOnly_IsDrawnOnBlack_ForTheWholeLength()
    {
        var editor = ProjectEditor.CreateNew();
        editor.AddText(S(3));                                // 3–8

        var plan = Plan(editor);
        var graph = FilterGraph(plan);

        Assert.DoesNotContain(plan.Arguments, a => a == "-i");
        Assert.Contains("color=c=black:s=1920x1080:r=30:d=8,", graph);
        Assert.Contains("drawtext=", graph);
        Assert.Equal(S(8), plan.Duration);
    }

    [Fact]
    public void TextOnALowerTrack_IsCoveredByVideoAbove()
    {
        var editor = ProjectEditor.CreateNew();
        editor.AddText();                                    // on Video 1 (bottom)
        var top = editor.AddVideoTrack();
        editor.AddToTimeline(editor.AddMedia(Video("a.mp4", 10)).Id, null, top.Id);

        var graph = FilterGraph(Plan(editor));

        Assert.True(graph.IndexOf("drawtext=", StringComparison.Ordinal) < graph.IndexOf("overlay=", StringComparison.Ordinal));
        Assert.Contains("format=yuva420p", graph);           // the video above keeps see-through gaps
    }

    [Fact]
    public void TextOnAnUpperTrack_IsDrawnAfterStacking()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));
        editor.AddText(videoTrackId: editor.AddVideoTrack().Id);

        var graph = FilterGraph(Plan(editor));

        Assert.DoesNotContain("overlay=", graph);            // nothing to stack: text goes straight on Video 1
        Assert.True(graph.IndexOf("concat=", StringComparison.Ordinal) < 0
                    || graph.IndexOf("concat=", StringComparison.Ordinal) < graph.IndexOf("drawtext=", StringComparison.Ordinal));
    }

    [Fact]
    public void SameFont_IsCopiedOnce_EachTextGetsItsOwnFile()
    {
        var editor = ProjectEditor.CreateNew();
        editor.AddText(style: new TextStyle { Text = "One" });
        editor.AddText(style: new TextStyle { Text = "Two" });
        editor.AddText(style: new TextStyle { Text = "Three", FontFamily = "Impact" });

        var plan = Plan(editor);

        Assert.Equal(["font0.ttf", "font1.ttf"], plan.Files.Where(f => f.CopyFrom is not null).Select(f => f.Name));
        Assert.Equal(["One", "Two", "Three"], plan.Files.Where(f => f.Text is not null).Select(f => f.Text));
        Assert.Contains("fontfile=font1.ttf:textfile=text2.txt", FilterGraph(plan));
    }

    [Fact]
    public void EmptyText_IsSkipped_AndWindowsLineEndingsAreNormalised()
    {
        var editor = ProjectEditor.CreateNew();
        editor.AddText(style: new TextStyle { Text = "   " });
        editor.AddText(style: new TextStyle { Text = "Line 1\r\nLine 2\r\n" });

        var plan = Plan(editor);

        Assert.Equal("Line 1\nLine 2", plan.Files.Single(f => f.Text is not null).Text);
        Assert.Single(FilterGraph(plan).Split("drawtext=")[1..]);
    }

    [Fact]
    public void MissingFont_GivesAPlainMessage()
    {
        var editor = ProjectEditor.CreateNew();
        editor.AddText(style: new TextStyle { FontFamily = "Comic Nope" });

        var ex = Assert.Throws<ExportException>(() => Plan(editor, _ => null));

        Assert.Contains("\"Comic Nope\"", ex.Message);
        Assert.Contains("isn't installed", ex.Message);
    }

    [Fact]
    public void HiddenTrack_DrawsNoText()
    {
        var editor = ProjectEditor.CreateNew();
        editor.Place(Video("a.mp4", 10));
        var top = editor.AddVideoTrack();
        editor.AddText(videoTrackId: top.Id);
        editor.SetTrackMuted(top.Id, true);

        var plan = Plan(editor, _ => throw new InvalidOperationException("no font lookup expected"));

        Assert.DoesNotContain("drawtext", FilterGraph(plan));
        Assert.Empty(plan.Files);
    }

    [Fact]
    public void Numbers_UseDots_EvenInRomanian()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ro-RO");
            var editor = ProjectEditor.CreateNew();
            var clip = editor.AddText(S(1.5), style: new TextStyle { X = 0.25 });
            editor.SetFade(clip.Id, ClipEdge.Left, S(0.75));

            var graph = FilterGraph(Plan(editor));

            Assert.Contains("gte(t,1.5)*lt(t,6.5)", graph);
            Assert.Contains("x=(w-text_w)*0.25", graph);
            Assert.Contains("(t-1.5)/0.75", graph);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---------------------------------------------------------- font lookup

    [Theory]
    [InlineData("Arial Black (TrueType)", "Arial Black", true)]
    [InlineData("arial black (TrueType)", "Arial Black", true)]
    [InlineData("Arial (TrueType)", "Arial Black", false)]
    [InlineData("Arial Black Italic (TrueType)", "Arial Black", false)]
    [InlineData("Cambria & Cambria Math (TrueType)", "Cambria Math", true)]
    [InlineData("Bahnschrift (OpenType)", "Bahnschrift", true)]
    public void RegistryFontNames_AreMatchedByFamily(string valueName, string family, bool expected) =>
        Assert.Equal(expected, FontLocator.RegistryNameMatches(valueName, family));

    [Fact]
    public void ArialBlack_IsFoundOnWindows()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "ariblk.ttf");
        Assert.SkipUnless(OperatingSystem.IsWindows() && File.Exists(expected), "Needs Windows with Arial Black installed.");

        Assert.Equal(expected, FontLocator.Find("Arial Black"), ignoreCase: true);
    }

    [Fact]
    public void UnknownFont_IsNotFound() =>
        Assert.Null(FontLocator.Find("CSharpNLVED Font That Does Not Exist"));
}
