using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TagMarks.Desktop.Views;

/// <summary>
/// A plain error dialog for a problem that stops TagMarks from starting (spec §11 #53). The app exits
/// when it is closed.
/// </summary>
public sealed class StartupErrorWindow : Window {
    public StartupErrorWindow(string message) {
        Title = "TagMarks";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // A tray-only app may not be frontmost at startup; keep the dialog where the user sees it.
        Topmost = true;

        var quit = new Button {
            Content = "Quit",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        quit.Click += (_, _) => Close();

        Content = new StackPanel {
            Margin = new Thickness(20),
            Spacing = 16,
            MaxWidth = 520,
            Children = {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                quit,
            },
        };
        Opened += (_, _) => Activate();
    }
}
