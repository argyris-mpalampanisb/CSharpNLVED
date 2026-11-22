using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CSharpNLVED.App;

/// <summary>Small message boxes (Avalonia doesn't ship one).</summary>
public static class Dialogs
{
    /// <summary>Shows a message with buttons; returns the clicked button's text, or null if closed.</summary>
    public static async Task<string?> AskAsync(Window owner, string title, string message, params string[] buttons)
    {
        string? result = null;

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        for (var i = 0; i < buttons.Length; i++)
        {
            var text = buttons[i];
            var button = new Button { Content = text, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0)
                button.Classes.Add("accent");
            button.Click += (_, _) =>
            {
                result = text;
                dialog.Close();
            };
            buttonRow.Children.Add(button);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttonRow,
            },
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    public static Task ShowErrorAsync(Window owner, string title, string message) =>
        AskAsync(owner, title, message, "OK");

    /// <summary>
    /// Asks for a (possibly multi-line) text. Enter adds a line, Ctrl+Enter or OK accepts,
    /// Escape or Cancel gives null.
    /// </summary>
    public static async Task<string?> AskTextAsync(Window owner, string title, string message, string initial)
    {
        string? result = null;

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var box = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            FontSize = 16,
        };

        void Accept()
        {
            result = box.Text ?? "";
            dialog.Close();
        }

        var ok = new Button { Content = "OK", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => dialog.Close();

        // Tunnel, so Ctrl+Enter is seen before the text box turns it into a new line.
        var accel = OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control;
        dialog.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                dialog.Close();
            }
            else if (e.Key == Avalonia.Input.Key.Enter && e.KeyModifiers.HasFlag(accel))
            {
                e.Handled = true;
                Accept();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel },
                },
            },
        };

        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
