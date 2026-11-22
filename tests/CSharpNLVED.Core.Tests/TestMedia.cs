using CSharpNLVED.Core.Projects;

namespace CSharpNLVED.Core.Tests;

/// <summary>Fake media items for tests (no real files needed unless a test creates them).</summary>
internal static class TestMedia
{
    public static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    public static MediaItem Video(string name, double seconds, bool withSound = true) => new()
    {
        Path = Path.Combine(Path.GetTempPath(), name),
        Duration = S(seconds),
        HasVideo = true,
        HasAudio = withSound,
    };

    public static MediaItem Song(string name, double seconds) => new()
    {
        Path = Path.Combine(Path.GetTempPath(), name),
        Duration = S(seconds),
        HasAudio = true,
    };

    /// <summary>Adds the media to the project and places it on the timeline; returns the created clips (video first).</summary>
    public static IReadOnlyList<Clip> Place(this ProjectEditor editor, MediaItem media, double? at = null) =>
        editor.AddToTimeline(editor.AddMedia(media).Id, at is null ? null : S(at.Value));

    public static Track V1(this ProjectEditor editor) => editor.Project.VideoTracks[0];
    public static Track A1(this ProjectEditor editor) => editor.Project.AudioTracks[0];
    public static Track Music(this ProjectEditor editor) => editor.Project.AudioTracks[^1];
}
