using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpNLVED.Core.Projects;

/// <summary>Reads and writes .csnlved project files (readable JSON).</summary>
public static class ProjectFile
{
    public const string Extension = ".csnlved";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(Project project) => JsonSerializer.Serialize(project, Options);

    /// <param name="baseDirectory">Folder of the project file, used to find media that moved along with it.</param>
    public static Project FromJson(string json, string? baseDirectory = null)
    {
        var project = JsonSerializer.Deserialize<Project>(json, Options)
                      ?? throw new InvalidDataException("The project file is empty.");

        if (project.FormatVersion > Project.CurrentFormatVersion)
            throw new InvalidDataException("This project was made with a newer version of CSharpNLVED. Update CSharpNLVED to open it.");

        // Guard against hand-edited files with missing sections.
        project.Settings ??= new ProjectSettings();
        project.Media ??= [];
        project.VideoTracks ??= [];
        project.AudioTracks ??= [];

        if (project.FormatVersion < 2)
            UpgradeFromVersion1(project);
        project.FormatVersion = Project.CurrentFormatVersion; // 2 → 3 needs no changes

        if (project.VideoTracks.Count == 0)
            project.VideoTracks.Add(new Track { Name = "Video 1", Kind = TrackKind.Video });
        foreach (var track in project.AllTracks)
            track.SortClips();

        ResolveMedia(project, baseDirectory);
        return project;
    }

    /// <summary>
    /// Version 1 had one video track whose clips played back to back and carried their
    /// own sound. Version 2 places every clip at a position and keeps sound on audio tracks,
    /// so: lay clips out end to end, and give each video clip a linked audio clip on a new
    /// "Audio 1" track that takes over the old video track's volume and mute.
    /// </summary>
    private static void UpgradeFromVersion1(Project project)
    {
        var video = project.LegacyVideoTrack ?? new Track();
        project.LegacyVideoTrack = null;
        video.Name = "Video 1";
        video.Kind = TrackKind.Video;
        LayOutEndToEnd(video);

        var videoSound = new Track { Name = "Audio 1", Kind = TrackKind.Audio, Volume = video.Volume, Muted = video.Muted };
        foreach (var clip in video.Clips)
        {
            if (project.FindMedia(clip.MediaId) is not { HasAudio: true })
                continue;

            clip.LinkId = Ids.New();
            videoSound.Clips.Add(new Clip
            {
                MediaId = clip.MediaId,
                Start = clip.Start,
                In = clip.In,
                Out = clip.Out,
                LinkId = clip.LinkId,
            });
        }

        video.Volume = 1.0;
        video.Muted = false;

        foreach (var track in project.AudioTracks)
        {
            track.Kind = TrackKind.Audio;
            LayOutEndToEnd(track);
        }

        project.VideoTracks = [video];
        project.AudioTracks.Insert(0, videoSound);
        project.FormatVersion = Project.CurrentFormatVersion;
    }

    private static void LayOutEndToEnd(Track track)
    {
        var position = TimeSpan.Zero;
        foreach (var clip in track.Clips)
        {
            clip.Start = position;
            position += clip.Length;
        }
    }

    public static async Task<Project> LoadAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var json = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        try
        {
            return FromJson(json, Path.GetDirectoryName(fullPath));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} isn't a valid CSharpNLVED project ({ex.Message}).", ex);
        }
    }

    /// <summary>Saves via a temporary file, so a crash mid-save can't destroy the existing project.</summary>
    public static async Task SaveAsync(Project project, string path, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        foreach (var media in project.Media)
            media.RelativePath = Path.GetRelativePath(directory, media.Path);

        var temp = fullPath + ".tmp";
        await File.WriteAllTextAsync(temp, ToJson(project), ct).ConfigureAwait(false);
        File.Move(temp, fullPath, overwrite: true);
    }

    private static void ResolveMedia(Project project, string? baseDirectory)
    {
        foreach (var media in project.Media)
        {
            // Absolute path gone? Try the path relative to the project (folder was moved/copied).
            if (!File.Exists(media.Path) && baseDirectory is not null && !string.IsNullOrEmpty(media.RelativePath))
            {
                var candidate = Path.GetFullPath(Path.Combine(baseDirectory, media.RelativePath));
                if (File.Exists(candidate))
                    media.Path = candidate;
            }

            media.IsMissing = !File.Exists(media.Path);
        }
    }
}
