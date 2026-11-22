using CSharpNLVED.Core.Projects;
using Xunit;

namespace CSharpNLVED.Core.Tests;

public sealed class ProjectFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csnlved-tests-" + Guid.NewGuid().ToString("N"));

    public ProjectFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task SaveAndOpen_RoundTrips()
    {
        var mediaPath = CreateFakeMedia(Path.Combine(_root, "clip.mp4"));
        var projectPath = Path.Combine(_root, "test" + ProjectFile.Extension);

        var editor = ProjectEditor.CreateNew();
        var media = editor.AddMedia(new MediaItem { Path = mediaPath, Duration = TimeSpan.FromSeconds(10), HasVideo = true, HasAudio = true });
        var clips = editor.AddToTimeline(media.Id);
        editor.TrimEdgeTo(clips[0].Id, ClipEdge.Left, TimeSpan.FromSeconds(1));
        editor.TrimEdgeTo(clips[0].Id, ClipEdge.Right, TimeSpan.FromSeconds(8));
        editor.SetTrackVolume(editor.Project.AudioTracks[0].Id, 0.25);
        await editor.SaveAsync(projectPath, TestContext.Current.CancellationToken);

        Assert.False(editor.IsDirty);

        var reopened = await ProjectEditor.OpenAsync(projectPath, TestContext.Current.CancellationToken);
        var p = reopened.Project;
        Assert.Single(p.Media);
        Assert.False(p.Media[0].IsMissing);
        var video = p.VideoTracks[0].Clips[0];
        Assert.Equal((TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8)), (video.In, video.Out));
        Assert.Equal(TimeSpan.FromSeconds(1), video.Start);
        Assert.Equal(video.LinkId, p.AudioTracks[0].Clips[0].LinkId);
        Assert.Equal(0.25, p.AudioTracks[0].Volume);
        Assert.Equal(TrackKind.Audio, p.AudioTracks[0].Kind);
        Assert.Equal("test", reopened.DisplayName);
    }

    [Fact]
    public async Task MovedProjectFolder_StillFindsItsMedia()
    {
        var folderA = Path.Combine(_root, "A");
        var mediaPath = CreateFakeMedia(Path.Combine(folderA, "media", "clip.mp4"));
        var projectPath = Path.Combine(folderA, "trip" + ProjectFile.Extension);

        var editor = ProjectEditor.CreateNew();
        editor.AddMedia(new MediaItem { Path = mediaPath, Duration = TimeSpan.FromSeconds(3), HasVideo = true });
        await editor.SaveAsync(projectPath, TestContext.Current.CancellationToken);

        // Move the whole folder, like copying a project to a USB stick.
        var folderB = Path.Combine(_root, "B");
        Directory.Move(folderA, folderB);

        var reopened = await ProjectEditor.OpenAsync(Path.Combine(folderB, "trip" + ProjectFile.Extension), TestContext.Current.CancellationToken);
        var media = reopened.Project.Media[0];
        Assert.False(media.IsMissing);
        Assert.Equal(Path.Combine(folderB, "media", "clip.mp4"), media.Path);
    }

    [Fact]
    public void MissingMedia_IsFlagged()
    {
        var project = Project.CreateDefault();
        project.Media.Add(new MediaItem { Path = Path.Combine(_root, "gone.mp4"), Duration = TimeSpan.FromSeconds(1), HasVideo = true });

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(project), _root);

        Assert.True(loaded.Media[0].IsMissing);
    }

    [Fact]
    public void ProjectFromANewerCSharpNLVED_IsRefused()
    {
        var project = Project.CreateDefault();
        project.FormatVersion = Project.CurrentFormatVersion + 1;

        Assert.Throws<InvalidDataException>(() => ProjectFile.FromJson(ProjectFile.ToJson(project)));
    }

    [Fact]
    public void Version1Project_IsUpgraded()
    {
        // A project saved by CSharpNLVED 0.1: one video track whose clips play back to back and carry their sound.
        var mediaPath = CreateFakeMedia(Path.Combine(_root, "a.mp4"));
        var json = $$"""
        {
          "formatVersion": 1,
          "media": [
            { "id": "m1", "path": {{System.Text.Json.JsonSerializer.Serialize(mediaPath)}}, "duration": "00:00:10", "hasVideo": true, "hasAudio": true },
            { "id": "m2", "path": {{System.Text.Json.JsonSerializer.Serialize(mediaPath + "2")}}, "duration": "00:00:05", "hasVideo": true, "hasAudio": false }
          ],
          "videoTrack": {
            "id": "v", "name": "Video", "kind": "Video", "volume": 0.5, "muted": false,
            "clips": [
              { "id": "c1", "mediaId": "m1", "in": "00:00:02", "out": "00:00:10" },
              { "id": "c2", "mediaId": "m2", "in": "00:00:00", "out": "00:00:05" }
            ]
          },
          "audioTracks": [
            { "id": "music", "name": "Music", "kind": "Audio", "volume": 0.3, "clips": [] }
          ]
        }
        """;

        var project = ProjectFile.FromJson(json, _root);

        Assert.Equal(Project.CurrentFormatVersion, project.FormatVersion);
        Assert.Null(project.LegacyVideoTrack);
        var video = Assert.Single(project.VideoTracks);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(8) }, video.Clips.Select(c => c.Start));

        // The sound of a.mp4 moved to a new linked "Audio 1" track that kept the old volume.
        var sound = project.AudioTracks[0];
        Assert.Equal("Audio 1", sound.Name);
        Assert.Equal(0.5, sound.Volume);
        var soundClip = Assert.Single(sound.Clips);   // b.mp4 has no sound
        Assert.Equal(video.Clips[0].LinkId, soundClip.LinkId);
        Assert.Equal("Music", project.AudioTracks[1].Name);
    }

    private static string CreateFakeMedia(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0, 1, 2, 3 });
        return path;
    }
}
