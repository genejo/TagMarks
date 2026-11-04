namespace Cauldron.Services;

/// <summary>
/// Lets one call at a time reach the inner clipboard. TextCopy's Windows clipboard has no locking: one thread's
/// CloseClipboard can free the memory another thread is still reading, which corrupted the native heap (TASK-78).
/// </summary>
public class SynchronizedClipboardService(IClipboardService inner) : IClipboardService {
   private readonly Lock _lock = new();

   public string? GetTextFirstLine() {
      lock (_lock) return inner.GetTextFirstLine();
   }

   public string? GetText() {
      lock (_lock) return inner.GetText();
   }

   public void SetText(string text) {
      lock (_lock) inner.SetText(text);
   }
}
