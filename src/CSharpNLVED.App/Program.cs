using Avalonia;

namespace CSharpNLVED.App;

internal static class Program
{
    // Don't touch Avalonia or any UI types before AppMain is called:
    // things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
