using System.Diagnostics;
using System.Globalization;
using System.Text;
using CSharpNLVED.Core.FFmpeg;

namespace CSharpNLVED.Core.Export;

/// <summary>Runs an <see cref="ExportPlan"/> with FFmpeg, reporting progress and supporting cancel.</summary>
public static class Exporter
{
    public static async Task ExportAsync(
        FFmpegInstallation ffmpeg,
        ExportPlan plan,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        var missing = plan.InputFiles
            .Concat(plan.Files.Select(f => f.CopyFrom).OfType<string>())
            .Where(f => !File.Exists(f)).Select(f => Path.GetFileName(f)).ToList();
        if (missing.Count > 0)
            throw new ExportException($"These files are missing: {string.Join(", ", missing)}");

        Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputPath)!);
        TryDelete(plan.TempOutputPath);

        var workDirectory = PrepareWorkFiles(plan);
        try
        {
            await RunFFmpegAsync(ffmpeg, plan, workDirectory, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            if (workDirectory is not null)
                TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    /// Writes the plan's helper files (drawtext text, font copies) into a fresh folder that
    /// FFmpeg then runs in. Returns null if the plan needs none.
    /// </summary>
    private static string? PrepareWorkFiles(ExportPlan plan)
    {
        if (plan.Files.Count == 0)
            return null;

        var directory = Path.Combine(AppPaths.CacheDirectory, $"export-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in plan.Files)
            {
                var target = Path.Combine(directory, file.Name);
                if (file.CopyFrom is { } source)
                    File.Copy(source, target);
                else
                    File.WriteAllText(target, file.Text ?? "", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(directory);
            throw new ExportException($"Couldn't prepare the text for export: {ex.Message}");
        }
    }

    private static async Task RunFFmpegAsync(
        FFmpegInstallation ffmpeg,
        ExportPlan plan,
        string? workDirectory,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workDirectory is not null)
            psi.WorkingDirectory = workDirectory; // the command names its helper files without a path
        foreach (var arg in plan.Arguments)
            psi.ArgumentList.Add(arg);

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();

        // FFmpeg writes errors to stderr and "key=value" progress lines to stdout (-progress pipe:1).
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var killOnCancel = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already exited */ }
        });

        var encoded = TimeSpan.Zero;
        double? speed = null;
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
        {
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq];
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0:
                    encoded = TimeSpan.FromMicroseconds(us);
                    break;

                case "speed" when double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && x > 0:
                    speed = x;
                    break;

                case "progress":
                    progress?.Report(MakeProgress(plan.Duration, encoded, speed));
                    break;
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();

        var logPath = WriteLog(plan, process.ExitCode, stopwatch.Elapsed, stderr);

        if (ct.IsCancellationRequested)
        {
            TryDelete(plan.TempOutputPath);
            throw new OperationCanceledException(ct);
        }

        if (process.ExitCode != 0 || !File.Exists(plan.TempOutputPath))
        {
            TryDelete(plan.TempOutputPath);
            var tail = string.Join(" / ", stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeLast(3));
            var encoderFailed = LooksLikeEncoderFailure(stderr, plan.EncoderName);
            var message = encoderFailed
                ? $"The GPU encoder ({plan.EncoderName}) failed: {tail}"
                : $"FFmpeg stopped with an error: {tail}";
            throw new ExportException(message, logPath, encoderFailed);
        }

        File.Move(plan.TempOutputPath, plan.OutputPath, overwrite: true);
        progress?.Report(new ExportProgress(1, plan.Duration, speed, TimeSpan.Zero));
    }

    private static ExportProgress MakeProgress(TimeSpan total, TimeSpan encoded, double? speed)
    {
        var fraction = total > TimeSpan.Zero ? Math.Clamp(encoded / total, 0, 1) : 0;
        TimeSpan? remaining = speed is > 0 ? (total - encoded) / speed.Value : null;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        return new ExportProgress(fraction, encoded, speed, remaining);
    }

    private static bool LooksLikeEncoderFailure(string stderr, string encoderName) =>
        stderr.Contains(encoderName, StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("OpenEncodeSessionEx", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("CUDA_ERROR", StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps a log of every export in data/logs, handy when something goes wrong.</summary>
    private static string? WriteLog(ExportPlan plan, int exitCode, TimeSpan elapsed, string stderr)
    {
        try
        {
            var path = Path.Combine(AppPaths.LogsDirectory, $"export-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var log = new StringBuilder()
                .AppendLine($"CSharpNLVED {AppInfo.Version} on {AppInfo.RuntimeId}")
                .AppendLine($"Output: {plan.OutputPath}")
                .AppendLine($"Exit code: {exitCode}, took {elapsed}")
                .AppendLine()
                .AppendLine(plan.DisplayCommand)
                .AppendLine()
                .AppendLine(stderr);
            File.WriteAllText(path, log.ToString());
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
