using Avalonia.Controls;

namespace TagMarks.Desktop.Services;

public interface IWindowFocusService {
    void BringToForeground(Window window);
}
