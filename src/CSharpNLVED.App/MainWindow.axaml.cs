using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CSharpNLVED.Core;
using CSharpNLVED.Core.FFmpeg;
using CSharpNLVED.Core.Gpu;
using CSharpNLVED.Core.Projects;
using Track = CSharpNLVED.Core.Projects.Track;

namespace CSharpNLVED.App;

public partial class MainWindow : Window
{
    private static readonly IBrush NormalStatus = new SolidColorBrush(Color.Parse("#8A8F98"));
    private static readonly IBrush WarningStatus = new SolidColorBrush(Color.Parse("#F0B45A"));

    private readonly FFmpegManager _ffmpeg = new();
    private readonly GpuEncoderManager _gpu = new();

    private static readonly FilePickerFileType ProjectFileType = new("CSharpNLVED project")
    {
        Patterns = ["*" + ProjectFile.Extension],
    };

    /// <summary>The build the banner's button will install; null means the button retries the check.</summary>
    private FFmpegRelease? _offered;
    private bool _busy;

    private ProjectEditor _project = ProjectEditor.CreateNew();
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        StatusRight.Text = $"v{AppInfo.Version}  •  {AppInfo.RuntimeId}";
        var mode = AppPaths.IsPortable ? "portable" : "user data";
        ToolTip.SetTip(StatusRight, $"Data folder ({mode}): {AppPaths.DataDirectory}");

        BannerAction.Click += OnBannerActionClick;
        BannerDismiss.Click += (_, _) => FFmpegBanner.IsVisible = false;
        GpuButton.Click += OnGpuButtonClick;

        NewItem.Click += async (_, _) => await NewProjectAsync();
        OpenItem.Click += async (_, _) => await OpenProjectAsync();
        SaveItem.Click += async (_, _) => await SaveProjectAsync(saveAs: false);
        SaveAsItem.Click += async (_, _) => await SaveProjectAsync(saveAs: true);
        ExitItem.Click += (_, _) => Close();
        AddClipsItem.Click += async (_, _) => await AddClipsAsync();
        AddClipsButton.Click += async (_, _) => await AddClipsAsync();
        ExportItem.Click += async (_, _) => await OpenExportAsync();
        ExportButton.Click += async (_, _) => await OpenExportAsync();
        UndoItem.Click += (_, _) => _project.Undo();
        RedoItem.Click += (_, _) => _project.Redo();
        KeyDown += OnKeyDown;
        SetUpTimeline();
        SetUpDragAndDrop();
        Closing += OnClosing;

        AttachProject(_project);

        // Check FFmpeg once the window is visible, so startup never feels blocked.
        Opened += async (_, _) => await CheckFFmpegAsync();
    }

    // --------------------------------------------------------------- project

    private void AttachProject(ProjectEditor editor)
    {
        _project.Changed -= OnProjectChanged;
        _project = editor;
        _project.Changed += OnProjectChanged;
        _project.AutoRipple = RippleToggle.IsChecked == true; // the toggle applies to every project
        Timeline.SetEditor(editor);
        OnProjectChanged(this, EventArgs.Empty);
    }

    private void OnProjectChanged(object? sender, EventArgs e)
    {
        Title = $"{(_project.IsDirty ? "• " : "")}{_project.DisplayName} — {AppInfo.Name} {AppInfo.Version}";

        UndoItem.IsEnabled = _project.CanUndo;
        UndoItem.Header = _project.UndoDescription is { } undo ? $"_Undo {undo}" : "_Undo";
        RedoItem.IsEnabled = _project.CanRedo;
        RedoItem.Header = _project.RedoDescription is { } redo ? $"_Redo {redo}" : "_Redo";

        var project = _project.Project;
        var clips = project.AllTracks.Sum(t => t.Clips.Count);
        TimelinePlaceholder.Text = clips == 0
            ? "Timeline   •   empty   •   add clips with \"Add clips…\" above"
            : $"{clips} clips   •   {Clock(project.Duration)} long   •   drag clips to reorder, drag their edges to trim";

        MediaEmptyText.IsVisible = project.Media.Count == 0;
        MediaList.ItemsSource = project.Media.Select(m =>
        {
            var kind = m.HasVideo ? $"{m.Width}×{m.Height}" : "audio";
            var missing = m.IsMissing ? "   ⚠ missing" : "";
            return $"{m.FileName}\n{Clock(m.Duration)}   •   {kind}{missing}";
        }).ToList();

        RefreshTrackHeaders();
        Timeline.Refresh();
        UpdateClipButtons();
    }

    private static string Clock(TimeSpan t) => TimelineControl.Clock(t);

    // --------------------------------------------------------------- timeline

    private bool _syncingScroll;

    private void SetUpTimeline()
    {
        Timeline.SetEditor(_project);
        Timeline.SelectionChanged += (_, _) => UpdateClipButtons();
        Timeline.ViewChanged += (_, _) => SyncScrollBar();
        TimelineScroll.ValueChanged += (_, e) =>
        {
            if (!_syncingScroll)
                Timeline.ScrollSeconds = e.NewValue;
        };

        Timeline.EditRefused += (_, message) => TimelinePlaceholder.Text = message;

        SplitButton.Click += (_, _) => SplitAtPlayhead();
        RemoveClipButton.Click += (_, _) => RemoveSelectedClips();
        UnlinkButton.Click += (_, _) => ToggleLink();
        RippleToggle.Click += (_, _) => _project.AutoRipple = RippleToggle.IsChecked == true;
        AddVideoTrackButton.Click += (_, _) => _project.AddVideoTrack();
        AddTextButton.Click += async (_, _) => await AddTextAsync();
        AddTextItem.Click += async (_, _) => await AddTextAsync();
        Timeline.TextClipDoubleClicked += async (_, clipId) => await EditTextAsync(clipId);
        TextEditor.StyleEdited += (_, style) =>
        {
            if (TextEditor.ClipId is { } clipId)
                TryEdit(() => _project.SetTextStyle(clipId, style));
        };
        AddAudioTrackButton.Click += (_, _) => _project.AddAudioTrack();
        FitButton.Click += (_, _) => Timeline.ZoomToFit();

        var sortMenu = new MenuFlyout();
        sortMenu.Items.Add(CreateMenuItem("By file name (clip2 before clip10)", () => SortTrack(SortOrder.Name)));
        sortMenu.Items.Add(CreateMenuItem("By file date (oldest first)", () => SortTrack(SortOrder.Date)));
        sortMenu.Items.Add(CreateMenuItem("Reverse order", () => SortTrack(SortOrder.Reverse)));
        SortButton.Flyout = sortMenu;

        static MenuItem CreateMenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void SyncScrollBar()
    {
        _syncingScroll = true;
        try
        {
            var max = Timeline.MaxScrollSeconds;
            TimelineScroll.Maximum = max;
            TimelineScroll.ViewportSize = Math.Max(Timeline.VisibleSeconds, 0.001);
            TimelineScroll.Value = Math.Min(Timeline.ScrollSeconds, max);
            TimelineScroll.IsEnabled = max > 0.01;
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void UpdateClipButtons()
    {
        var selected = Timeline.SelectedClipIds;
        RemoveClipButton.IsEnabled = selected.Count > 0;
        SplitButton.IsEnabled = _project.Project.AllTracks.Any(t => t.Clips.Count > 0);

        var primary = Timeline.PrimaryClipId;
        var linked = primary is not null && _project.IsLinked(primary);
        UnlinkButton.Content = selected.Count >= 2 && !linked ? "Link" : "Unlink";
        UnlinkButton.IsEnabled = linked || selected.Count >= 2;

        UpdateTextPanel();
    }

    /// <summary>Shows the text panel while exactly one text clip is selected (and refreshes it after edits/undo).</summary>
    private void UpdateTextPanel()
    {
        var selected = Timeline.SelectedClipIds;
        if (selected.Count == 1 && _project.Project.FindClip(selected.First()) is { Clip: { Text: { } style } clip })
        {
            var settings = _project.Project.Settings;
            TextEditor.Load(clip.Id, style, settings.Width, settings.Height);
            TextEditor.IsVisible = true;
        }
        else
        {
            TextEditor.IsVisible = false;
        }
    }

    /// <summary>Removes the selected clips (and their linked clips).</summary>
    private void RemoveSelectedClips()
    {
        var selected = Timeline.SelectedClipIds.ToList();
        if (selected.Count == 0)
            return;

        TryEdit(() => _project.RemoveClips(selected));
        Timeline.ClearSelection();
    }

    /// <summary>U: unlink the selected clip from its partner, or link the Ctrl+clicked clips together.</summary>
    private void ToggleLink()
    {
        var selected = Timeline.SelectedClipIds.ToList();
        if (Timeline.PrimaryClipId is { } primary && _project.IsLinked(primary) && selected.Count == 1)
        {
            TryEdit(() => _project.Unlink(primary));
            TimelinePlaceholder.Text = "Unlinked: video and sound now move separately. Undo (Ctrl+Z) links them again.";
        }
        else if (selected.Count >= 2)
        {
            TryEdit(() => _project.Link(selected));
            TimelinePlaceholder.Text = "Linked: these clips now move, trim and split together.";
        }
        else if (Timeline.PrimaryClipId is { } single && _project.IsLinked(single))
        {
            TryEdit(() => _project.Unlink(single));
        }

        UpdateClipButtons();
    }

    /// <summary>
    /// S: cuts at the playhead. With clips selected, cuts those (and their linked clips);
    /// otherwise cuts every clip under the playhead.
    /// </summary>
    private void SplitAtPlayhead()
    {
        var selected = Timeline.SelectedClipIds.ToList();
        IReadOnlyList<Clip> created = [];
        TryEdit(() => created = _project.Split(Timeline.Playhead, selected.Count > 0 ? selected : null));

        if (created.Count > 0)
            Timeline.SelectClips(created.Select(c => c.Id));
        else
            TimelinePlaceholder.Text = "Put the red playhead over a clip (click the ruler), then press S. It can't be right at a clip's edge.";
    }

    /// <summary>T / "+ Text": a text clip at the playhead, above the video there, then asks what it says.</summary>
    private async Task AddTextAsync()
    {
        Clip? clip = null;
        TryEdit(() => clip = _project.AddTextOverVideo(Timeline.Playhead));
        if (clip is null)
            return;

        Timeline.SelectClips([clip.Id]);
        await EditTextAsync(clip.Id);
    }

    /// <summary>Asks for a text clip's new text (double-click). The full text panel comes in Phase 3 step 4.</summary>
    private async Task EditTextAsync(string clipId)
    {
        try
        {
            if (_project.Project.FindClip(clipId) is not { Clip.Text: { } style })
                return;

            var text = await Dialogs.AskTextAsync(this, "Text",
                "What should it say? Enter starts a new line; Ctrl+Enter or OK when done.", style.Text);
            if (text is null)
                return;

            // Look the clip up again: the project may have changed while the dialog was open.
            if (_project.Project.FindClip(clipId) is { Clip.Text: { } current })
                TryEdit(() => _project.SetTextStyle(clipId, current with { Text = text }));
        }
        catch (Exception ex)
        {
            TimelinePlaceholder.Text = $"Couldn't edit the text: {ex.Message}";
        }
    }

    private void TryEdit(Action edit)
    {
        try
        {
            edit();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            TimelinePlaceholder.Text = ex.Message;
        }
    }

    private enum SortOrder { Name, Date, Reverse }

    private void SortTrack(SortOrder order)
    {
        var project = _project.Project;
        var trackId = Timeline.SelectedTrackId ?? project.VideoTracks[0].Id;
        if (project.FindTrack(trackId) is not { Clips.Count: > 1 } track)
            return;

        string FileName(Clip c) => project.FindMedia(c.MediaId)?.FileName ?? "";
        DateTime FileDate(Clip c) => project.FindMedia(c.MediaId) is { } m && File.Exists(m.Path)
            ? File.GetLastWriteTimeUtc(m.Path)
            : DateTime.MaxValue;

        IEnumerable<Clip> ordered;
        string description;
        switch (order)
        {
            case SortOrder.Name:
                ordered = track.Clips.OrderBy(FileName, NaturalStringComparer.Instance);
                description = "Sort by name";
                break;
            case SortOrder.Date:
                ordered = track.Clips.OrderBy(FileDate);
                description = "Sort by date";
                break;
            default:
                ordered = track.Clips.AsEnumerable().Reverse();
                description = "Reverse order";
                break;
        }

        TryEdit(() => _project.ReorderClips(track.Id, ordered.Select(c => c.Id).ToList(), description));
    }

    // ---------------------------------------------------------- track headers

    private sealed record HeaderControls(TextBlock Name, ToggleButton Mute, Slider Volume, TextBlock Percent);

    private readonly Dictionary<string, HeaderControls> _headers = [];
    private List<string> _headerKeys = [];
    private bool _updatingHeaders;

    /// <summary>
    /// The left column: one header per track, the same height as its timeline lane.
    /// Controls are only rebuilt when tracks are added/removed; otherwise just updated,
    /// so dragging a volume slider isn't interrupted by its own changes.
    /// </summary>
    private void RefreshTrackHeaders()
    {
        var tracks = _project.Project.AllTracks.ToList();
        var keys = tracks.Select(t => t.Id).ToList();
        if (!keys.SequenceEqual(_headerKeys))
        {
            BuildTrackHeaders(tracks);
            _headerKeys = keys;
        }

        _updatingHeaders = true;
        try
        {
            foreach (var track in tracks)
            {
                var h = _headers[track.Id];
                h.Name.Text = track.Name;
                if (Math.Abs(h.Volume.Value - track.Volume) > 1e-6)
                    h.Volume.Value = Math.Min(track.Volume, h.Volume.Maximum);
                h.Mute.IsChecked = track.Muted;
                h.Percent.Text = track.Kind == TrackKind.Video
                    ? (track.Muted ? "hidden" : "")
                    : track.Muted ? "muted" : VolumeText(track.Volume);
            }
        }
        finally
        {
            _updatingHeaders = false;
        }
    }

    /// <summary>"100% · 0.0 dB", like the level readout in Vegas.</summary>
    private static string VolumeText(double gain) =>
        gain <= 0 ? "0% · −∞ dB" : $"{gain:P0} · {20 * Math.Log10(gain):+0.0;−0.0;0.0} dB";

    private void BuildTrackHeaders(List<Track> tracks)
    {
        TrackHeaders.Children.Clear();
        _headers.Clear();
        TrackHeaders.Children.Add(new Border { Height = TimelineControl.RulerHeight });

        foreach (var track in tracks)
        {
            var trackId = track.Id;

            var name = new TextBlock
            {
                FontWeight = FontWeight.SemiBold,
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var mute = new ToggleButton { Content = "M", FontSize = 11, Padding = new Avalonia.Thickness(7, 1), Margin = new Avalonia.Thickness(4, 0, 0, 0) };
            ToolTip.SetTip(mute, track.Kind == TrackKind.Video ? "Hide this track (it won't be exported)" : "Mute this track's sound");
            mute.Click += (_, _) =>
            {
                if (!_updatingHeaders)
                    _project.SetTrackMuted(trackId, mute.IsChecked == true);
            };

            var volume = new Slider
            {
                Minimum = 0,
                Maximum = 2,
                SmallChange = 0.05,
                LargeChange = 0.1,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, -6, 0, -6),
                IsVisible = track.Kind == TrackKind.Audio, // video tracks have no sound of their own
            };
            ToolTip.SetTip(volume, "Volume (0–200%). Double-click to reset to 100%.");
            volume.ValueChanged += (_, e) =>
            {
                if (!_updatingHeaders)
                    _project.SetTrackVolume(trackId, e.NewValue);
            };
            volume.DoubleTapped += (_, _) => _project.SetTrackVolume(trackId, 1.0);

            var percent = new TextBlock
            {
                FontSize = 11,
                MinWidth = 42,
                Margin = new Avalonia.Thickness(6, 0, 0, 0),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = NormalStatus,
            };

            var topRow = new DockPanel();
            var canRemove = track.Kind == TrackKind.Audio || tracks.Count(t => t.Kind == TrackKind.Video) > 1;
            if (canRemove)
            {
                var remove = new Button { Content = "✕", FontSize = 10, Padding = new Avalonia.Thickness(6, 1), Margin = new Avalonia.Thickness(4, 0, 0, 0) };
                ToolTip.SetTip(remove, "Remove this track and its clips (Ctrl+Z brings it back)");
                remove.Click += (_, _) => TryEdit(() => _project.RemoveTrack(trackId));
                DockPanel.SetDock(remove, Dock.Right);
                topRow.Children.Add(remove);
            }

            DockPanel.SetDock(mute, Dock.Right);
            topRow.Children.Add(mute);
            topRow.Children.Add(name);

            var bottomRow = new DockPanel();
            DockPanel.SetDock(percent, Dock.Right);
            bottomRow.Children.Add(percent);
            bottomRow.Children.Add(volume);

            var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            Grid.SetRow(bottomRow, 1);
            content.Children.Add(topRow);
            content.Children.Add(bottomRow);

            TrackHeaders.Children.Add(new Border
            {
                Height = TimelineControl.LaneHeight(track.Kind),
                Margin = new Avalonia.Thickness(0, 0, 0, TimelineControl.LaneGap),
                Background = new SolidColorBrush(Color.Parse("#1B1D23")),
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(8, 4),
                Child = content,
            });

            _headers[track.Id] = new HeaderControls(name, mute, volume, percent);
        }
    }

    private async Task NewProjectAsync()
    {
        if (await ConfirmDiscardChangesAsync())
            AttachProject(ProjectEditor.CreateNew());
    }

    private async Task OpenProjectAsync()
    {
        if (!await ConfirmDiscardChangesAsync())
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            AllowMultiple = false,
            FileTypeFilter = [ProjectFileType],
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null)
            return;

        try
        {
            AttachProject(await ProjectEditor.OpenAsync(path));

            var missing = _project.Project.Media.Where(m => m.IsMissing).Select(m => m.FileName).ToList();
            if (missing.Count > 0)
                await Dialogs.ShowErrorAsync(this, "Missing media",
                    $"These files couldn't be found:\n\n{string.Join("\n", missing)}");
        }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync(this, "Couldn't open project", ex.Message);
        }
    }

    /// <returns>True if the project was saved.</returns>
    private async Task<bool> SaveProjectAsync(bool saveAs)
    {
        var path = _project.FilePath;
        if (saveAs || path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save project",
                SuggestedFileName = _project.DisplayName + ProjectFile.Extension,
                DefaultExtension = ProjectFile.Extension.TrimStart('.'),
                FileTypeChoices = [ProjectFileType],
                ShowOverwritePrompt = true,
            });

            path = file?.TryGetLocalPath();
            if (path is null)
                return false;
        }

        try
        {
            await _project.SaveAsync(path);
            return true;
        }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync(this, "Couldn't save project", ex.Message);
            return false;
        }
    }

    /// <summary>Asks to save unsaved changes. Returns false if the user cancelled.</summary>
    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!_project.IsDirty)
            return true;

        var answer = await Dialogs.AskAsync(this, "Unsaved changes",
            $"Save changes to \"{_project.DisplayName}\" first?", "Save", "Don't save", "Cancel");

        return answer switch
        {
            "Save" => await SaveProjectAsync(saveAs: false),
            "Don't save" => true,
            _ => false,
        };
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !_project.IsDirty)
            return;

        e.Cancel = true; // Decide after asking; then close for real.
        if (await ConfirmDiscardChangesAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Timeline keys without Ctrl. Sliders/buttons that use these keys themselves
        // mark them handled first, so they don't reach this point.
        // ...and never while typing in a text box (S, T, U, Delete are just letters there).
        if (e.KeyModifiers == KeyModifiers.None && FocusManager?.GetFocusedElement() is not TextBox)
        {
            switch (e.Key)
            {
                case Key.Delete or Key.Back:
                    e.Handled = true;
                    RemoveSelectedClips();
                    return;
                case Key.S:
                    e.Handled = true;
                    SplitAtPlayhead();
                    return;
                case Key.U:
                    e.Handled = true;
                    ToggleLink();
                    return;
                case Key.T:
                    e.Handled = true;
                    await AddTextAsync();
                    return;
                case Key.Home:
                    e.Handled = true;
                    Timeline.Playhead = TimeSpan.Zero;
                    return;
                case Key.End:
                    e.Handled = true;
                    Timeline.Playhead = TimeSpan.MaxValue;
                    return;
            }
        }

        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (!e.KeyModifiers.HasFlag(primary))
            return;

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.N:
                e.Handled = true;
                await NewProjectAsync();
                break;
            case Key.O:
                e.Handled = true;
                await OpenProjectAsync();
                break;
            case Key.S:
                e.Handled = true;
                await SaveProjectAsync(saveAs: shift);
                break;
            case Key.Z:
                e.Handled = true;
                if (shift) _project.Redo(); else _project.Undo();
                break;
            case Key.Y:
                e.Handled = true;
                _project.Redo();
                break;
            case Key.I:
                e.Handled = true;
                await AddClipsAsync();
                break;
            case Key.E:
                e.Handled = true;
                await OpenExportAsync();
                break;
        }
    }

    // ------------------------------------------------------ add clips / export

    private static readonly FilePickerFileType MediaFileType = new("Video and audio")
    {
        Patterns =
        [
            "*.mp4", "*.mkv", "*.mov", "*.m4v", "*.webm", "*.avi", "*.mts", "*.m2ts", "*.ts", "*.wmv", "*.flv", "*.3gp",
            "*.mp3", "*.wav", "*.flac", "*.aac", "*.m4a", "*.ogg", "*.opus",
        ],
    };

    /// <summary>File extensions CSharpNLVED imports (also used to pick files out of dropped folders).</summary>
    private static readonly HashSet<string> MediaExtensions =
        MediaFileType.Patterns!.Select(p => p.TrimStart('*').ToLowerInvariant()).ToHashSet();

    /// <summary>Where imported files go.</summary>
    private abstract record ImportTarget
    {
        /// <summary>After the last clip (into the selected track, if it's the right kind).</summary>
        public sealed record Append : ImportTarget;

        /// <summary>At a time on the timeline, on the given track if it's the right kind; one after another.</summary>
        public sealed record At(TimeSpan Time, string? TrackId) : ImportTarget;

        /// <summary>Only into the project's media list.</summary>
        public sealed record MediaOnly : ImportTarget;
    }

    private async Task AddClipsAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add clips",
            AllowMultiple = true,
            FileTypeFilter = [MediaFileType, FilePickerFileTypes.All],
        });

        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        await ImportAsync(paths, new ImportTarget.Append());
    }

    /// <summary>
    /// Reads each file with ffprobe, adds it to the project's media and (unless MediaOnly)
    /// puts it on the timeline. Folders are opened and their video/audio files added in
    /// natural name order (clip2 before clip10).
    /// </summary>
    private async Task ImportAsync(IReadOnlyList<string> paths, ImportTarget target)
    {
        if (paths.Count == 0)
            return;

        if (_ffmpeg.Current is not { } ffmpeg)
        {
            await Dialogs.ShowErrorAsync(this, "FFmpeg needed", "CSharpNLVED needs FFmpeg to read video files. Download it from the banner at the top first.");
            return;
        }

        var files = ExpandFolders(paths);
        if (files.Count == 0)
        {
            TimelinePlaceholder.Text = "No video or audio files there.";
            return;
        }

        var problems = new List<string>();
        var nextPosition = target is ImportTarget.At at ? at.Time : (TimeSpan?)null;
        var targetTrack = target switch
        {
            ImportTarget.At { TrackId: { } id } => _project.Project.FindTrack(id),
            ImportTarget.Append when Timeline.SelectedTrackId is { } id => _project.Project.FindTrack(id),
            _ => null,
        };

        var added = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            TimelinePlaceholder.Text = $"Reading {Path.GetFileName(files[i])} ({i + 1}/{files.Count})…";
            try
            {
                var media = _project.AddMedia(await Core.Media.MediaProbe.ProbeAsync(ffmpeg, files[i]));
                if (target is ImportTarget.MediaOnly)
                    continue;

                // Into the target track if it's the right kind; otherwise the defaults
                // (top video track, first audio track for a video's sound, last audio track for music).
                var clips = _project.AddToTimeline(media.Id, nextPosition,
                    videoTrackId: targetTrack is { Kind: TrackKind.Video } ? targetTrack.Id : null,
                    audioTrackId: targetTrack is { Kind: TrackKind.Audio } && !media.HasVideo ? targetTrack.Id : null);

                added.AddRange(clips.Select(c => c.Id));
                if (nextPosition is not null && clips.Count > 0)
                    nextPosition = clips.Max(c => c.End); // several files: one after another
            }
            catch (Exception ex)
            {
                problems.Add(ex.Message);
            }
        }

        OnProjectChanged(this, EventArgs.Empty);
        if (added.Count > 0)
            Timeline.SelectClips(added);

        if (problems.Count > 0)
            await Dialogs.ShowErrorAsync(this, "Some files weren't added", string.Join("\n", problems));
    }

    /// <summary>Files stay as they are; folders are replaced by the media files inside them.</summary>
    private static List<string> ExpandFolders(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    result.AddRange(Directory.EnumerateFiles(path)
                        .Where(f => MediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable folder: skip it.
                }
            }
            else if (File.Exists(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    // ------------------------------------------------- drag and drop from the OS

    private void SetUpDragAndDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => Timeline.ShowDropMarker(null));
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Double-click a file in the Media panel: add it to the end of the timeline.
        MediaList.DoubleTapped += (_, _) =>
        {
            var index = MediaList.SelectedIndex;
            var media = _project.Project.Media;
            if (index >= 0 && index < media.Count)
                TryEdit(() => Timeline.SelectClips(_project.AddToTimeline(media[index].Id).Select(c => c.Id)));
        };
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 })
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        Timeline.ShowDropMarker(IsOver(Timeline, e) ? e.GetPosition(Timeline) : null);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        Timeline.ShowDropMarker(null);
        var paths = (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0)
            return;

        ImportTarget target;
        if (IsOver(Timeline, e))
        {
            var (time, trackId) = Timeline.DropTargetAt(e.GetPosition(Timeline));
            target = new ImportTarget.At(time, trackId);
        }
        else if (IsOver(MediaPanel, e))
        {
            target = new ImportTarget.MediaOnly();
        }
        else
        {
            target = new ImportTarget.Append();
        }

        try
        {
            await ImportAsync(paths, target);
        }
        catch (Exception ex)
        {
            TimelinePlaceholder.Text = $"Couldn't add the dropped files: {ex.Message}";
        }
    }

    private static bool IsOver(Avalonia.Visual control, DragEventArgs e)
    {
        var p = e.GetPosition(control);
        return p.X >= 0 && p.Y >= 0 && p.X < control.Bounds.Width && p.Y < control.Bounds.Height;
    }

    private async Task OpenExportAsync()
    {
        if (_ffmpeg.Current is not { } ffmpeg)
        {
            await Dialogs.ShowErrorAsync(this, "FFmpeg needed", "CSharpNLVED needs FFmpeg to export. Download it from the banner at the top first.");
            return;
        }

        if (_gpu.Report is not { HasWorkingEncoder: true } report)
        {
            await Dialogs.ShowErrorAsync(this, "No working GPU encoder",
                "CSharpNLVED exports with your graphics card only, and no working GPU encoder was found. " +
                "Click \"GPU encoders…\" in the status bar to see why and what to try.");
            return;
        }

        if (_project.Project.Duration <= TimeSpan.Zero)
        {
            await Dialogs.ShowErrorAsync(this, "Nothing to export", "Add at least one clip first (File → Add clips…).");
            return;
        }

        var window = new ExportWindow(_project, ffmpeg, report, onEncoderFailure: () =>
        {
            // The encoder worked before but failed now (driver changed?): forget and re-test.
            _gpu.Invalidate();
            _ = CheckEncodersAsync(forceRecheck: true);
        });
        await window.ShowDialog(this);
    }

    // ---------------------------------------------------------------- FFmpeg

    private async Task CheckFFmpegAsync()
    {
        try
        {
            StatusLeft.Text = "FFmpeg: checking…";
            var current = await _ffmpeg.LocateAsync();
            ShowCurrent(current);

            // Test the GPU encoders in parallel with the FFmpeg update check.
            var encoderCheck = CheckEncodersAsync(forceRecheck: false);

            var check = await _ffmpeg.CheckForUpdateAsync();
            if (check.Error is not null)
            {
                if (current is null)
                    ShowBanner($"FFmpeg not found, and the download server couldn't be reached: {check.Error}", "Retry", null);
                else
                    StatusLeft.Text += "   •   update check failed";
            }
            else if (check is { UpdateAvailable: true, Latest: { } latest })
            {
                var (message, action) = current switch
                {
                    null => ($"CSharpNLVED needs FFmpeg. Download FFmpeg {latest.Version} from {latest.Source}?", "Download"),
                    { IsManaged: false } => ($"Using your system FFmpeg ({current.VersionLine}). Get CSharpNLVED's own FFmpeg {latest.Version}? (recommended)", "Download"),
                    _ => ($"FFmpeg {latest.Version} is available (you have {current.VersionLine}).", "Update"),
                };
                ShowBanner(message, action, latest);
            }

            await encoderCheck;
        }
        catch (Exception ex)
        {
            StatusLeft.Text = $"FFmpeg check failed: {ex.Message}";
        }
    }

    private async void OnBannerActionClick(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_offered is null)
        {
            FFmpegBanner.IsVisible = false;
            await CheckFFmpegAsync();
            return;
        }

        _busy = true;
        BannerAction.IsEnabled = false;
        BannerDismiss.IsEnabled = false;
        BannerProgress.IsVisible = true;
        BannerProgress.IsIndeterminate = true;

        // Created on the UI thread, so its callback runs on the UI thread too.
        var progress = new Progress<InstallProgress>(p =>
        {
            BannerText.Text = p.Fraction is { } f ? $"{p.Stage}… {f:P0}" : $"{p.Stage}…";
            BannerProgress.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } value)
                BannerProgress.Value = value;
        });

        var installed = false;
        try
        {
            ShowCurrent(await _ffmpeg.InstallAsync(_offered, progress));
            FFmpegBanner.IsVisible = false;
            installed = true;
        }
        catch (Exception ex)
        {
            ShowBanner($"FFmpeg download failed: {ex.Message}", "Retry", _offered);
        }
        finally
        {
            _busy = false;
            BannerAction.IsEnabled = true;
            BannerDismiss.IsEnabled = true;
            BannerProgress.IsVisible = false;
        }

        // New FFmpeg build = new fingerprint, so this re-tests the encoders automatically.
        if (installed)
            await CheckEncodersAsync(forceRecheck: false);
    }

    private void ShowBanner(string message, string action, FFmpegRelease? offered)
    {
        _offered = offered;
        BannerText.Text = message;
        ToolTip.SetTip(BannerText, message);
        BannerAction.Content = action;
        FFmpegBanner.IsVisible = true;
    }

    private void ShowCurrent(FFmpegInstallation? installation)
    {
        StatusLeft.Text = installation is null
            ? "FFmpeg: not found"
            : $"FFmpeg {installation.VersionLine} ({(installation.IsManaged ? "managed by CSharpNLVED" : "system")})";
        ToolTip.SetTip(StatusLeft, installation?.FfmpegPath);
    }

    // ------------------------------------------------------------ GPU encoders

    private async Task<EncoderReport?> CheckEncodersAsync(bool forceRecheck)
    {
        if (_ffmpeg.Current is not { } ffmpeg)
        {
            GpuStatus.Text = "GPU: waiting for FFmpeg";
            GpuStatus.Foreground = NormalStatus;
            return null;
        }

        GpuStatus.Text = "GPU: checking encoders…";
        GpuStatus.Foreground = NormalStatus;
        try
        {
            var progress = new Progress<string>(s => GpuStatus.Text = $"GPU: {s}");
            var report = await _gpu.GetReportAsync(ffmpeg, forceRecheck, progress);
            ShowGpu(report);
            return report;
        }
        catch (Exception ex)
        {
            GpuStatus.Text = $"GPU check failed: {ex.Message}";
            GpuStatus.Foreground = WarningStatus;
            return null;
        }
    }

    private void ShowGpu(EncoderReport report)
    {
        // Results are in preference order, so the first working API is the one exports will use.
        var best = report.Results.Where(r => r.Works).GroupBy(r => r.Encoder.Api).FirstOrDefault();
        if (best is null)
        {
            GpuStatus.Text = "GPU: no working encoder (export disabled)";
            GpuStatus.Foreground = WarningStatus;
        }
        else
        {
            var codecs = string.Join(", ", best.Select(r => r.Encoder.Codec.DisplayName()));
            GpuStatus.Text = $"GPU: {best.Key.DisplayName()} ({codecs})";
            GpuStatus.Foreground = NormalStatus;
        }

        GpuButton.IsEnabled = true;
    }

    private async void OnGpuButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_gpu.Report is not { } report)
            return;

        var window = new EncodersWindow { Recheck = () => CheckEncodersAsync(forceRecheck: true) };
        window.ShowReport(report);
        await window.ShowDialog(this);
    }
}
