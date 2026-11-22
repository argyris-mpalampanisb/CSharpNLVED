using System.IO.Compression;
using System.Text.Json;

namespace CSharpNLVED.Core.FFmpeg;

/// <summary>
/// Finds FFmpeg, checks for newer builds, and downloads/installs CSharpNLVED's own copy
/// into data/tools/ffmpeg/bin.
///
/// CSharpNLVED only ever runs the ffmpeg/ffprobe programs (never loads FFmpeg's libraries),
/// so replacing them with a newer version is always safe.
/// </summary>
public sealed class FFmpegManager
{
    /// <summary>
    /// BtbN rebuilds the same release branch daily. We only offer such a rebuild
    /// once the installed one is this much older, to avoid nagging every day.
    /// </summary>
    private static readonly TimeSpan RebuildGrace = TimeSpan.FromDays(14);

    private static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : "";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IFFmpegSource? Source { get; } = FFmpegSources.ForCurrentPlatform();

    /// <summary>The FFmpeg CSharpNLVED will use, or null if none was found.</summary>
    public FFmpegInstallation? Current { get; private set; }

    public static string ManagedBinDirectory => Path.Combine(AppPaths.FFmpegDirectory, "bin");
    private static string ManifestPath => Path.Combine(AppPaths.FFmpegDirectory, "installed.json");

    // ---------------------------------------------------------------- locate

    /// <summary>Uses CSharpNLVED's own FFmpeg if present and working, otherwise one on the system PATH.</summary>
    public async Task<FFmpegInstallation?> LocateAsync(CancellationToken ct = default)
    {
        Current = null;

        var managedFfmpeg = Path.Combine(ManagedBinDirectory, "ffmpeg" + Exe);
        var managedProbe = Path.Combine(ManagedBinDirectory, "ffprobe" + Exe);
        if (File.Exists(managedFfmpeg) && File.Exists(managedProbe))
            Current = await ProbeAsync(managedFfmpeg, managedProbe, isManaged: true, ct).ConfigureAwait(false);

        if (Current is null && FindOnSystemPath() is { } system)
            Current = await ProbeAsync(system.Ffmpeg, system.Ffprobe, isManaged: false, ct).ConfigureAwait(false);

        return Current;
    }

    private static async Task<FFmpegInstallation?> ProbeAsync(string ffmpeg, string ffprobe, bool isManaged, CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpeg, ["-version"], TimeSpan.FromSeconds(10), ct)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
                return null;

            var firstLine = result.StdOut.Split('\n', 2)[0].Trim();
            return new FFmpegInstallation(ffmpeg, ffprobe, ParseVersionLine(firstLine), isManaged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // Missing, not executable, wrong architecture, timed out…
        }
    }

    /// <summary>"ffmpeg version 9.0.2-full_build-www.gyan.dev Copyright (c)…" → "9.0.2-full_build-www.gyan.dev"</summary>
    internal static string ParseVersionLine(string line)
    {
        const string prefix = "ffmpeg version ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return line;

        var rest = line[prefix.Length..];
        var cut = rest.IndexOf(" Copyright", StringComparison.Ordinal);
        return (cut >= 0 ? rest[..cut] : rest).Trim();
    }

    private static (string Ffmpeg, string Ffprobe)? FindOnSystemPath()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Apps started from Finder don't get the shell's PATH, so add Homebrew's usual places.
        if (OperatingSystem.IsMacOS())
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin"]);

        foreach (var dir in dirs)
        {
            try
            {
                var ffmpeg = Path.Combine(dir, "ffmpeg" + Exe);
                var ffprobe = Path.Combine(dir, "ffprobe" + Exe);
                if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                    return (ffmpeg, ffprobe);
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; skip it.
            }
        }

        return null;
    }

    // ---------------------------------------------------------- update check

    public async Task<FFmpegUpdateCheck> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (Source is null)
            return new FFmpegUpdateCheck(null, false, "There is no FFmpeg download source for this platform.");

        try
        {
            var latest = await Source.GetLatestAsync(ct).ConfigureAwait(false);
            return new FFmpegUpdateCheck(latest, IsUpdate(latest), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = ex is OperationCanceledException ? "the server took too long to answer" : ex.Message;
            return new FFmpegUpdateCheck(null, false, message);
        }
    }

    private bool IsUpdate(FFmpegRelease latest)
    {
        // No FFmpeg, or only a system one: always offer CSharpNLVED's own copy.
        if (Current is not { IsManaged: true })
            return true;

        var installed = ReadManifest();
        if (installed is null || installed.Source != latest.Source)
            return true;

        var installedVersion = VersionText.TryParseNumeric(installed.Version);
        var latestVersion = latest.NumericVersion;

        if (installedVersion is not null && latestVersion is not null && latestVersion > installedVersion)
            return true;

        // Same version, same build name, but rebuilt with fixes since (BtbN's daily builds).
        return latestVersion == installedVersion
               && latest.BuildId == installed.BuildId
               && latest.PublishedAt > installed.PublishedAt + RebuildGrace;
    }

    // --------------------------------------------------------------- install

    /// <summary>
    /// Downloads, verifies and installs <paramref name="release"/>. The previous copy is
    /// only replaced once the new one has been checked to run.
    /// </summary>
    public async Task<FFmpegInstallation> InstallAsync(
        FFmpegRelease release,
        IProgress<InstallProgress>? progress,
        CancellationToken ct = default)
    {
        var staging = Path.Combine(AppPaths.FFmpegDirectory, ".staging");
        TryDeleteDirectory(staging);
        Directory.CreateDirectory(staging);

        try
        {
            var extractDir = Path.Combine(staging, "extract");
            Directory.CreateDirectory(extractDir);

            for (var i = 0; i < release.Downloads.Count; i++)
            {
                var download = release.Downloads[i];
                var extension = download.Kind == ArchiveKind.Zip ? ".zip" : ".tar.xz";
                var archive = Path.Combine(staging, $"download{i}{extension}");
                var stage = release.Downloads.Count > 1
                    ? $"Downloading {i + 1}/{release.Downloads.Count}"
                    : "Downloading";

                await Net.Http.DownloadFileAsync(download.Url, archive, download.Sha256,
                    ThrottledDownloadProgress(stage, progress), ct).ConfigureAwait(false);

                progress?.Report(new InstallProgress("Extracting", null));
                await ExtractAsync(archive, download.Kind, extractDir, ct).ConfigureAwait(false);
            }

            // Collect ffmpeg + ffprobe from wherever they are inside the archive(s).
            var newBin = Path.Combine(staging, "bin");
            Directory.CreateDirectory(newBin);
            foreach (var tool in new[] { "ffmpeg", "ffprobe" })
            {
                var fileName = tool + Exe;
                var found = Directory.EnumerateFiles(extractDir, fileName, SearchOption.AllDirectories).FirstOrDefault()
                            ?? throw new FFmpegSourceException($"The download did not contain {fileName}.");

                var target = Path.Combine(newBin, fileName);
                File.Move(found, target);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101); // rwxr-xr-x
            }

            progress?.Report(new InstallProgress("Verifying", null));
            _ = await ProbeAsync(Path.Combine(newBin, "ffmpeg" + Exe), Path.Combine(newBin, "ffprobe" + Exe), true, ct)
                    .ConfigureAwait(false)
                ?? throw new FFmpegSourceException("The downloaded FFmpeg does not run on this system.");

            // Swap the new bin folder in.
            progress?.Report(new InstallProgress("Installing", null));
            var bin = ManagedBinDirectory;
            var old = bin + ".old";
            TryDeleteDirectory(old);
            if (Directory.Exists(bin))
                Directory.Move(bin, old);
            Directory.Move(newBin, bin);
            TryDeleteDirectory(old);

            WriteManifest(new InstalledFFmpegInfo(
                release.Source, release.Version, release.BuildId, release.PublishedAt, DateTimeOffset.UtcNow));
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        return await LocateAsync(ct).ConfigureAwait(false)
               ?? throw new FFmpegSourceException("FFmpeg was installed but could not be started.");
    }

    private static async Task ExtractAsync(string archive, ArchiveKind kind, string destination, CancellationToken ct)
    {
        switch (kind)
        {
            case ArchiveKind.Zip:
                await Task.Run(() => ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true), ct)
                    .ConfigureAwait(false);
                break;

            case ArchiveKind.TarXz:
                // .NET can read tar but not xz; every Linux system has tar with xz support.
                var result = await ProcessRunner.RunAsync("tar", ["-xJf", archive, "-C", destination],
                    TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
                if (result.ExitCode != 0)
                    throw new FFmpegSourceException($"Could not unpack the download: {result.StdErr.Trim()}");
                break;
        }
    }

    /// <summary>Turns byte counts into at most ~200 progress updates, so the UI isn't flooded.</summary>
    private static IProgress<(long Received, long? Total)>? ThrottledDownloadProgress(
        string stage, IProgress<InstallProgress>? progress)
    {
        if (progress is null)
            return null;

        var lastReported = -1.0;
        return new InlineProgress<(long Received, long? Total)>(p =>
        {
            if (p.Total is not > 0)
            {
                progress.Report(new InstallProgress(stage, null));
                return;
            }

            var fraction = (double)p.Received / p.Total.Value;
            if (fraction - lastReported >= 0.005 || fraction >= 1)
            {
                lastReported = fraction;
                progress.Report(new InstallProgress(stage, fraction));
            }
        });
    }

    // -------------------------------------------------------------- manifest

    public static InstalledFFmpegInfo? ReadManifest()
    {
        try
        {
            return File.Exists(ManifestPath)
                ? JsonSerializer.Deserialize<InstalledFFmpegInfo>(File.ReadAllText(ManifestPath), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteManifest(InstalledFFmpegInfo info) =>
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(info, JsonOptions));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are harmless; they get cleaned up next time.
        }
    }

    /// <summary>IProgress that calls straight through (the caller's Progress&lt;T&gt; handles UI threading).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
