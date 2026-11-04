namespace Cauldron.Services;

public interface IClipboardService {
    string? GetTextFirstLine();
    string? GetText();
    void SetText(string text);
}
