using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CSharpNLVED.Core.Export;
using CSharpNLVED.Core.FFmpeg;
using CSharpNLVED.Core.Gpu;
using CSharpNLVED.Core.Projects;

namespace CSharpNLVED.App;

public partial class ExportWindow : Window
{
    private static readonly string[] Extensions = [".mp4", ".mkv", ".mov"];

    private static readonly FilePickerFileType[] FileTypes =
    [
        new("MP4 video") { Patterns = ["*.mp4"] },
        new("MKV video") { Patterns = ["*.mkv"] },
        new("QuickTime video") { Patterns = ["*.mov"] },
    ];

    private static readonly ExportQuality[] Qualities = [ExportQuality.High, ExportQuality.Balanced, ExportQuality.Small];

    private readonly ProjectEditor? _editor;
    private readonly FFmpegInstallation? _ffmpeg;
    private readonly Action? _onEncoderFailure;
    private readonly List<EncoderTestResult> _encoders = [];

    private CancellationTokenSource? _cts;
    private string? _confirmedOverwrite;
    private string? _finishedFile;

    /// <summary>For the XAML designer only.</summary>
    public ExportWindow()
    {
        InitializeComponent();
    }

    /// <param name="onEncoderFailure">Called when the GPU encoder failed, so the app can re-test encoders.</param>
    public ExportWindow(ProjectEditor editor, FFmpegInstallation ffmpeg, EncoderReport encoders, Action onEncoderFailure)
        : this()
    {
        _editor = editor;
        _ffmpeg = ffmpeg;
        _onEncoderFailure = onEncoderFailure;

        // One entry per codec, using the preferred working GPU for it.
        foreach (var codec in new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 })
        {
            if (encoders.Preferred(codec) is { } result)
                _encoders.Add(result);
        }

        CodecBox.ItemsSource = _encoders.Select(Describe).ToList();
        CodecBox.SelectedIndex = 0;
        QualityBox.ItemsSource = new[] { "High (bigger file)", "Balanced", "Small file" };
        QualityBox.SelectedIndex = 1;
        OutputBox.Text = DefaultOutputPath(editor);

        var project = editor.Project;
        var s = project.Settings;
        SummaryText.Text = $"{s.Width}×{s.Height}, {s.FrameRate.Value:0.##} fps   •   " +
                           $"{project.VideoTracks.Sum(t => t.Clips.Count)} video clips   •   {Format(project.Duration)} long";

        BrowseButton.Click += OnBrowseClick;
        ExportButton.Click += OnExportClick;
        CloseButton.Click += OnCloseClick;
        ShowFolderButton.Click += (_, _) => ShowInFolder();
        Closing += (_, _) => _cts?.Cancel(); // closing mid-export cancels it
    }

    private static string Describe(EncoderTestResult result)
    {
        var note = result.Encoder.Codec switch
        {
            VideoCodec.H264 => "plays everywhere",
            VideoCodec.Hevc => "smaller files, modern devices",
            VideoCodec.Av1 => "smallest files, newest devices",
            _ => "",
        };
        return $"{result.Encoder.Codec.DisplayName()} — {result.Encoder.Api.DisplayName()}   ({note})";
    }

    private static string DefaultOutputPath(ProjectEditor editor)
    {
        var folder = editor.FilePath is { } projectFile
            ? Path.GetDirectoryName(projectFile)!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(folder, editor.DisplayName + ".mp4");
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var current = OutputBox.Text ?? "";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export video as",
            SuggestedFileName = Path.GetFileName(current),
            DefaultExtension = "mp4",
            FileTypeChoices = FileTypes,
            ShowOverwritePrompt = true,
        });

        if (file?.TryGetLocalPath() is { } path)
        {
            OutputBox.Text = path;
            _confirmedOverwrite = path; // the save dialog already asked
        }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_editor is null || _ffmpeg is null || _cts is not null)
            return;

        if (_encoders.Count == 0 || CodecBox.SelectedIndex < 0)
        {
            StatusText.Text = "No working GPU encoder. Open \"GPU encoders…\" in the status bar to see why.";
            return;
        }

        var path = (OutputBox.Text ?? "").Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            StatusText.Text = "Choose where to save the video (a full path, or use Browse…).";
            return;
        }

        if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            path += ".mp4";
            OutputBox.Text = path;
        }

        if (File.Exists(path) && path != _confirmedOverwrite)
        {
            var answer = await Dialogs.AskAsync(this, "Replace file?",
                $"{Path.GetFileName(path)} already exists. Replace it?", "Replace", "Cancel");
            if (answer != "Replace")
                return;
        }

        ExportPlan plan;
        try
        {
            var encoder = _encoders[CodecBox.SelectedIndex];
            var quality = Qualities[Math.Clamp(QualityBox.SelectedIndex, 0, Qualities.Length - 1)];
            plan = ExportPlanner.Build(_editor.Project, encoder, quality, path);
        }
        catch (ExportException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        SetRunning(true);
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<ExportProgress>(p =>
        {
            ExportProgressBar.Value = p.Fraction;
            var parts = new List<string> { $"{p.Fraction:P0}" };
            if (p.Speed is { } speed)
                parts.Add($"{speed:0.0}× real time");
            if (p.Remaining is { } left)
                parts.Add($"{Format(left)} left");
            StatusText.Text = "Exporting…   " + string.Join("   •   ", parts);
        });

        _cts = new CancellationTokenSource();
        try
        {
            await Exporter.ExportAsync(_ffmpeg, plan, progress, _cts.Token);
            _finishedFile = plan.OutputPath;
            StatusText.Text = $"Done in {Format(stopwatch.Elapsed)}. Saved {Path.GetFileName(plan.OutputPath)}.";
            ShowFolderButton.IsVisible = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Export cancelled.";
        }
        catch (ExportException ex)
        {
            StatusText.Text = ex.LogPath is null ? ex.Message : $"{ex.Message}\n\nFull log: {ex.LogPath}";
            if (ex.EncoderFailed)
            {
                StatusText.Text += "\n\nRe-testing your GPU encoders…";
                _onEncoderFailure?.Invoke();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_cts is not null)
            _cts.Cancel(); // while exporting, this button is "Cancel"
        else
            Close();
    }

    private void SetRunning(bool running)
    {
        ExportButton.IsEnabled = !running;
        BrowseButton.IsEnabled = !running;
        OutputBox.IsEnabled = !running;
        CodecBox.IsEnabled = !running;
        QualityBox.IsEnabled = !running;
        CloseButton.Content = running ? "Cancel" : "Close";
        ExportProgressBar.IsVisible = running;
        if (running)
        {
            ExportProgressBar.Value = 0;
            ShowFolderButton.IsVisible = false;
            StatusText.Text = "Starting…";
        }
    }

    private void ShowInFolder()
    {
        if (_finishedFile is null)
            return;

        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", $"/select,\"{_finishedFile}\"");
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", ["-R", _finishedFile]);
            else
                Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_finishedFile)!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open the folder: {ex.Message}";
        }
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
