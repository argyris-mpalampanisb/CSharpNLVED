using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CSharpNLVED.Core.Gpu;

namespace CSharpNLVED.App;

/// <summary>Shows which GPU encoders work, why the others don't, and what to try.</summary>
public partial class EncodersWindow : Window
{
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#4CC38A"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F06A6A"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8A8F98"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#FF6B4A"));

    /// <summary>Runs a forced re-test and returns the new report.</summary>
    public Func<Task<EncoderReport?>>? Recheck { get; set; }

    public EncodersWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        RecheckButton.Click += OnRecheckClick;
    }

    public void ShowReport(EncoderReport report)
    {
        var panel = ReportPanel.Children;
        panel.Clear();

        panel.Add(Heading("Graphics adapters"));
        if (report.Adapters.Count == 0)
            panel.Add(Text("None detected.", Dim));
        foreach (var adapter in report.Adapters)
        {
            var driver = adapter.DriverVersion ?? adapter.KernelDriver;
            panel.Add(Text(driver is null ? adapter.Name : $"{adapter.Name}  •  driver {driver}"));
        }

        panel.Add(Heading("Encoders"));
        foreach (var result in report.Results)
            panel.Add(EncoderRow(result));

        if (report.Hints.Count > 0)
        {
            panel.Add(Heading("What to try"));
            foreach (var hint in report.Hints)
                panel.Add(Text("• " + hint));
        }

        panel.Add(Text($"Tested {report.TestedAt.ToLocalTime():g}. Re-tested automatically when FFmpeg, " +
                       "the kernel or a GPU driver changes.", Dim, margin: new Thickness(0, 16, 0, 0)));
    }

    private static Control EncoderRow(EncoderTestResult result)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,240,*") };

        var mark = new TextBlock
        {
            Text = result.Works ? "✓" : "✗",
            Foreground = result.Works ? Good : Bad,
            FontWeight = FontWeight.Bold,
        };

        var name = new TextBlock { Text = $"{result.Encoder.DisplayName}  ({result.Encoder.FfmpegName})" };
        Grid.SetColumn(name, 1);

        var status = new TextBlock
        {
            Text = result.Works ? (result.Device is null ? "Works" : $"Works ({result.Device})") : result.Problem,
            Foreground = result.Works ? Good : Dim,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(status, 2);
        if (result.Details is not null)
            ToolTip.SetTip(status, result.Details);

        grid.Children.Add(mark);
        grid.Children.Add(name);
        grid.Children.Add(status);
        return grid;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        Foreground = Accent,
        Margin = new Thickness(0, 12, 0, 2),
    };

    private static TextBlock Text(string text, IBrush? foreground = null, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (foreground is not null)
            block.Foreground = foreground;
        if (margin is { } m)
            block.Margin = m;
        return block;
    }

    private async void OnRecheckClick(object? sender, RoutedEventArgs e)
    {
        if (Recheck is null)
            return;

        RecheckButton.IsEnabled = false;
        BusyText.Text = "Testing encoders…";
        try
        {
            var report = await Recheck();
            if (report is not null)
                ShowReport(report);
            BusyText.Text = "";
        }
        catch (Exception ex)
        {
            BusyText.Text = $"Re-check failed: {ex.Message}";
        }
        finally
        {
            RecheckButton.IsEnabled = true;
        }
    }
}
