namespace Cauldron.Services;

public class ClipboardService : IClipboardService {
   public string? GetTextFirstLine() { 
      // Avalonia may provide smth like this. 
      // https://github.com/AvaloniaUI/Avalonia/discussions/11170#discussioncomment-10714434
      // todo: low priority - see if we should use that instead of ClipboardService
      string? firstLine = TextCopy.ClipboardService.GetText()?.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)[0];
      return firstLine;
   }

   public string? GetText() {
      return TextCopy.ClipboardService.GetText();
   }

   public void SetText(string text) {
      TextCopy.ClipboardService.SetText(text);
   }
}