using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CSharpNLVED.Core.Projects;
using CSharpNLVED.Core.Text;

namespace CSharpNLVED.App;

/// <summary>
/// Settings for the selected text clip: text, font, size, colours, outline and position,
/// with a preview. Every change is raised through <see cref="StyleEdited"/> right away;
/// the main window passes it to <see cref="ProjectEditor.SetTextStyle"/> (so it's checked
/// and undoable) and calls <see cref="Load"/> again after any project change.
/// </summary>
public partial class TextPanel : UserControl
{
    private static readonly (string Name, string Hex)[] SwatchColors =
    [
        ("White", "#FFFFFF"), ("Black", "#000000"), ("Grey", "#9AA0A6"), ("Yellow", "#FFD400"),
        ("Orange", "#FF8C1A"), ("Red", "#E53935"), ("Pink", "#FF4FA3"), ("Purple", "#9B59FF"),
        ("Blue", "#2F80ED"), ("Cyan", "#22D3EE"), ("Green", "#43D65B"),
    ];

    // Position presets: near an edge (a small margin) or centred.
    private static readonly double[] SpotsX = [0.04, 0.5, 0.96];
    private static readonly double[] SpotsY = [0.06, 0.5, 0.94];

    private static readonly Lazy<List<string>> InstalledFonts = new(() =>
        FontManager.Current.SystemFonts
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList());

    private readonly Border[,] _positionButtons = new Border[3, 3];
    private TextStyle _style = new();
    private int _frameWidth = 1920;
    private int _frameHeight = 1080;
    private bool _loading;

    public TextPanel()
    {
        InitializeComponent();

        foreach (var font in InstalledFonts.Value)
            FontBox.Items.Add(FontItem(font));

        BuildSwatches(TextSwatches, hex => Apply(_style with { Color = hex }));
        BuildSwatches(OutlineSwatches, hex => Apply(_style with { OutlineColor = hex }));
        BuildPositionGrid();

        TextEntry.TextChanged += (_, _) =>
        {
            if (!_loading)
                Apply(_style with { Text = TextEntry.Text ?? "" });
        };
        FontBox.SelectionChanged += (_, _) =>
        {
            if (!_loading && FontBox.SelectedItem is ComboBoxItem { Tag: string font })
                Apply(_style with { FontFamily = font });
        };
        SizeBox.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is { } size)
                Apply(_style with { FontSize = (double)size });
        };
        OutlineBox.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is { } width)
                Apply(_style with { OutlineWidth = (double)width });
        };
        TextColorBox.TextChanged += (_, _) =>
        {
            // Only once it's a whole colour; half-typed values are ignored.
            if (!_loading && TextColorBox.Text?.Trim() is { } hex && ProjectEditor.IsColor(hex))
                Apply(_style with { Color = hex });
        };
        OutlineColorBox.TextChanged += (_, _) =>
        {
            if (!_loading && OutlineColorBox.Text?.Trim() is { } hex && ProjectEditor.IsColor(hex))
                Apply(_style with { OutlineColor = hex });
        };
        XSlider.ValueChanged += (_, e) =>
        {
            if (!_loading)
                Apply(_style with { X = Math.Round(e.NewValue) / 100 });
        };
        YSlider.ValueChanged += (_, e) =>
        {
            if (!_loading)
                Apply(_style with { Y = Math.Round(e.NewValue) / 100 });
        };
    }

    /// <summary>The text clip being edited.</summary>
    public string? ClipId { get; private set; }

    /// <summary>Raised with the changed style after every edit in the panel.</summary>
    public event EventHandler<TextStyle>? StyleEdited;

    /// <summary>Shows a text clip's settings (also after undo/redo). Doesn't raise <see cref="StyleEdited"/>.</summary>
    public void Load(string clipId, TextStyle style, int frameWidth, int frameHeight)
    {
        ClipId = clipId;
        _style = style;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;

        _loading = true;
        try
        {
            // Only touch what differs, so the caret doesn't jump while typing.
            if (TextEntry.Text != style.Text)
                TextEntry.Text = style.Text;

            var fontItem = FontBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, style.FontFamily, StringComparison.OrdinalIgnoreCase));
            if (fontItem is null)
            {
                fontItem = FontItem(style.FontFamily); // e.g. a project from another computer
                FontBox.Items.Insert(0, fontItem);
            }

            if (!ReferenceEquals(FontBox.SelectedItem, fontItem))
                FontBox.SelectedItem = fontItem;

            SetIfDifferent(SizeBox, style.FontSize);
            SetIfDifferent(OutlineBox, style.OutlineWidth);
            SetIfDifferent(TextColorBox, style.Color);
            SetIfDifferent(OutlineColorBox, style.OutlineColor);
            XSlider.Value = style.X * 100;
            YSlider.Value = style.Y * 100;
        }
        finally
        {
            _loading = false;
        }

        FontWarning.IsVisible = FontLocator.Find(style.FontFamily) is null;
        ShowDerived();
    }

    private void Apply(TextStyle style)
    {
        _style = style;
        ShowDerived();
        StyleEdited?.Invoke(this, style);
    }

    /// <summary>Preview, % labels and the highlighted position preset.</summary>
    private void ShowDerived()
    {
        Preview.Show(_style, _frameWidth, _frameHeight);
        XLabel.Text = $"{_style.X * 100:0}%";
        YLabel.Text = $"{_style.Y * 100:0}%";

        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                var active = Math.Abs(_style.X - SpotsX[col]) < 0.005 && Math.Abs(_style.Y - SpotsY[row]) < 0.005;
                _positionButtons[row, col].Background = active ? Accent : PositionBrush;
            }
        }
    }

    // ------------------------------------------------------------ building

    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#FF6B4A"));
    private static readonly IBrush PositionBrush = new SolidColorBrush(Color.Parse("#2A2E37"));
    private static readonly IBrush SwatchBorder = new SolidColorBrush(Color.Parse("#55FFFFFF"));

    private static ComboBoxItem FontItem(string family) => new()
    {
        Content = family,
        Tag = family,
        FontFamily = new FontFamily(family), // each name shown in its own font
    };

    private static void BuildSwatches(Panel panel, Action<string> pick)
    {
        foreach (var (name, hex) in SwatchColors)
        {
            var swatch = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(3),
                Background = TextPreviewControl.ToBrush(hex),
                BorderBrush = SwatchBorder,
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(swatch, $"{name} ({hex})");
            swatch.Tapped += (_, _) => pick(hex);
            panel.Children.Add(swatch);
        }
    }

    private void BuildPositionGrid()
    {
        string[] rows = ["Top", "Middle", "Bottom"];
        string[] cols = ["left", "centre", "right"];
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                var (r, c) = (row, col);
                var button = new Border
                {
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Background = PositionBrush,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                ToolTip.SetTip(button, row == 1 && col == 1 ? "Centre" : $"{rows[row]} {cols[col]}");
                button.Tapped += (_, _) => Apply(_style with { X = SpotsX[c], Y = SpotsY[r] });
                Grid.SetRow(button, row);
                Grid.SetColumn(button, col);
                PositionGrid.Children.Add(button);
                _positionButtons[row, col] = button;
            }
        }
    }

    private static void SetIfDifferent(NumericUpDown box, double value)
    {
        var target = (decimal)Math.Round(value, 2);
        if (box.Value != target)
            box.Value = target;
    }

    private static void SetIfDifferent(TextBox box, string value)
    {
        if (!string.Equals(box.Text?.Trim(), value, StringComparison.OrdinalIgnoreCase))
            box.Text = value;
    }
}
