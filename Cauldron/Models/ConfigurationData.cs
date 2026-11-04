namespace Cauldron.Models;

public class ConfigurationData {
    public string TagMarksFolderPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TagMarks");
    
    public List<string> AutoTags { get; set; } =
        ["slack", "jira", "confluence", "obsidian", "google", "docs", "slides", "spreadsheets", "zoom", "notion"];

    /// <summary>
    /// Maximum time in milliseconds between the first and second tap of the activation
    /// hotkey for it to register as a double-tap. Clamped to 100–1000ms by the consumer.
    /// </summary>
    public uint DoubleTapMs { get; set; } = 400;

    /// <summary>
    /// The global hotkey that shows the window: <c>DoubleTap</c> (the platform default key),
    /// <c>DoubleTap:&lt;Key&gt;</c> or a gesture such as <c>Mod+Shift+Space</c>; one value for every
    /// platform or one per platform. Parsed by the desktop app, which falls back to the default on a bad value.
    /// </summary>
    public PerPlatformValue GlobalHotkey { get; set; } = PerPlatformValue.ForAll("DoubleTap");

    /// <summary>
    /// Folder that receives the daily zip backups of the TagMarks folder. Null (the default) turns
    /// backups off. A leading <c>~</c> is the user's home folder.
    /// </summary>
    public string? BackupFolderPath { get; set; }
}