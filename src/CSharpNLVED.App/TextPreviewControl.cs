using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CSharpNLVED.Core.Projects;

namespace CSharpNLVED.App;

/// <summary>
/// A quick look at a text clip: a frame-shaped box (project aspect ratio) with the text drawn
/// in its font, size, colours, outline and position, using the same rules as the export
/// (sizes relative to a 1080-high frame; X/Y place the text within the room around it).
/// It's an approximation: the real picture comes from FFmpeg's drawtext.
/// </summary>
public sealed class TextPreviewControl : Control
{
    private static readonly IBrush FrameBrush = new SolidColorBrush(Color.Parse("#000000"));
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#0E0F12"));
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.Parse("#3A3F48")), 1);
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.Parse("#22FFFFFF")), 1, new DashStyle([4, 4], 0));

    private TextStyle? _style;
    private double _aspect = 16.0 / 9;

    public void Show(TextStyle style, int width, int height)
    {
        _style = style;
        _aspect = width > 0 && height > 0 ? (double)width / height : 16.0 / 9;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        // The biggest frame of the project's shape that fits, centred.
        var available = Bounds.Size.Deflate(new Thickness(12));
        if (available.Width <= 0 || available.Height <= 0)
            return;
        var w = Math.Min(available.Width, available.Height * _aspect);
        var h = w / _aspect;
        var frame = new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);

        context.FillRectangle(FrameBrush, frame);
        context.DrawRectangle(FramePen, frame);
        // Rule-of-thirds guides help line titles up.
        for (var i = 1; i <= 2; i++)
        {
            context.DrawLine(GuidePen, new Point(frame.X + w * i / 3, frame.Y), new Point(frame.X + w * i / 3, frame.Bottom));
            context.DrawLine(GuidePen, new Point(frame.X, frame.Y + h * i / 3), new Point(frame.Right, frame.Y + h * i / 3));
        }

        if (_style is not { } style || string.IsNullOrWhiteSpace(style.Text))
            return;

        var scale = h / 1080.0;
        var text = new FormattedText(
            style.Text.Replace("\r\n", "\n").TrimEnd('\r', '\n'),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(style.FontFamily)),
            Math.Max(1, style.FontSize * scale),
            ToBrush(style.Color));

        // Line the lines up within the text block, like the export does.
        var blockWidth = text.WidthIncludingTrailingWhitespace;
        text.MaxTextWidth = blockWidth + 0.5;
        text.TextAlignment = style.LineAlignment switch
        {
            TextLineAlignment.Left => TextAlignment.Left,
            TextLineAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Center,
        };

        var origin = new Point(
            frame.X + (w - blockWidth) * style.X,
            frame.Y + (h - text.Height) * style.Y);

        using (context.PushClip(frame))
        {
            var geometry = style.OutlineWidth > 0 ? text.BuildGeometry(origin) : null;
            if (geometry is not null)
            {
                // drawtext's border goes around the letters: stroke twice as wide, then fill on top.
                var outline = new Pen(ToBrush(style.OutlineColor), style.OutlineWidth * scale * 2, lineJoin: PenLineJoin.Round);
                context.DrawGeometry(null, outline, geometry);
                context.DrawGeometry(ToBrush(style.Color), null, geometry);
            }
            else
            {
                context.DrawText(text, origin);
            }
        }
    }

    /// <summary>CSharpNLVED colours are "#RRGGBB[AA]"; Avalonia wants the alpha first ("#AARRGGBB").</summary>
    internal static IBrush ToBrush(string color)
    {
        var avalonia = color.Length == 9 ? "#" + color[7..9] + color[1..7] : color;
        return new SolidColorBrush(Color.TryParse(avalonia, out var parsed) ? parsed : Colors.White);
    }
}
