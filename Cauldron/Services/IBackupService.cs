namespace Cauldron.Services;

public enum BackupOutcome {
    /// <summary>No BackupFolderPath configured.</summary>
    Disabled,
    /// <summary>BackupFolderPath is the TagMarks folder or inside it.</summary>
    InvalidFolder,
    /// <summary>A new zip was written.</summary>
    Created,
    /// <summary>The newest zip already holds the current content, or there is nothing to back up.</summary>
    Unchanged,
    /// <summary>The newest zip is from today.</summary>
    NotDue,
    /// <summary>A zip with the same name appeared meanwhile (another machine, same minute).</summary>
    NameTaken,
    /// <summary>Reading or writing failed; the next check retries.</summary>
    Failed
}

public interface IBackupService {
    /// <summary>
    /// Zips the tagmark files into the backup folder if the newest backup is from an earlier day and
    /// the content changed since, then keeps only the newest backups. Never throws.
    /// </summary>
    BackupOutcome RunIfDue();
}
