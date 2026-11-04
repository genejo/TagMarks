using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cauldron.Managers;
using Microsoft.Extensions.Logging;

namespace Cauldron.Services;

/// <summary>
/// Daily zip backups of the TagMarks folder (spec §10.5). Several machines may share the TagMarks
/// folder and back up to the same synced folder, so nothing here relies on local state or locks:
/// the newest zip says when the last backup was made (its name) and what it holds (its comment).
/// </summary>
public partial class BackupService : IBackupService {
    public const int MaxBackups = 10;
    public const string FileNameSuffix = "_tagmarks";
    private const string FileNameFormat = "yyyy-MM-dd_HHmm";
    private const string FingerprintPrefix = "tagmarks-sha256:";
    // Sync clients don't upload "~*.tmp" files (Dropbox ignores them), so a half-written zip never syncs.
    private const string TempFilePrefix = "~tagmarks-";
    private const string TempFileExtension = ".tmp";
    private static readonly TimeSpan LeftoverTempFileAge = TimeSpan.FromHours(1);

    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<BackupService> _logger;
    private readonly ITagMarkSerializer _serializer;
    private readonly TimeProvider _timeProvider;

    // Also matches the names sync clients give to conflicting copies, e.g. "… (Gene's conflicted copy).zip".
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}_\d{4}_tagmarks.*\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex BackupFileNameRegex();

    public BackupService(IConfigurationManager configurationManager, ILogger<BackupService> logger,
        ITagMarkSerializer serializer, TimeProvider timeProvider) {
        _configurationManager = configurationManager;
        _logger = logger;
        _serializer = serializer;
        _timeProvider = timeProvider;
    }

    public static string ExpandHome(string path) {
        if (path != "~" && !path.StartsWith("~/") && !path.StartsWith(@"~\")) return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path[2..]);
    }

    public BackupOutcome RunIfDue() {
        var config = _configurationManager.Config;
        if (string.IsNullOrWhiteSpace(config.BackupFolderPath)) return BackupOutcome.Disabled;

        var tagMarksFolder = Path.GetFullPath(config.TagMarksFolderPath);
        var backupFolder = Path.GetFullPath(ExpandHome(config.BackupFolderPath.Trim()));
        if (IsSameOrInside(backupFolder, tagMarksFolder)) {
            _logger.LogWarning("Backups are off: the backup folder {BackupFolder} is inside the TagMarks folder {TagMarksFolder}",
                backupFolder, tagMarksFolder);
            return BackupOutcome.InvalidFolder;
        }

        try {
            Directory.CreateDirectory(backupFolder);
            DeleteLeftoverTempFiles(backupFolder);
            var outcome = BackUpIfDue(tagMarksFolder, backupFolder);
            Prune(Directory.EnumerateFiles(backupFolder));
            return outcome;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Backup to {BackupFolder} failed", backupFolder);
            return BackupOutcome.Failed;
        }
    }

    private BackupOutcome BackUpIfDue(string tagMarksFolder, string backupFolder) {
        var now = _timeProvider.GetLocalNow();
        var newest = BackupFiles(Directory.EnumerateFiles(backupFolder)).LastOrDefault();
        if (newest is not null && Path.GetFileName(newest).StartsWith(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) {
            _logger.LogDebug("Backup not due: {File} is from today", newest);
            return BackupOutcome.NotDue;
        }

        var files = ReadTagMarkFiles(tagMarksFolder);
        if (files.Count == 0) {
            _logger.LogDebug("Backup skipped: no tagmarks in {Folder}", tagMarksFolder);
            return BackupOutcome.Unchanged;
        }
        var fingerprint = Fingerprint(files);
        if (newest is not null && ReadFingerprint(newest) == fingerprint) {
            _logger.LogDebug("Backup skipped: {File} already holds the current tagmarks", newest);
            return BackupOutcome.Unchanged;
        }

        var target = Path.Combine(backupFolder,
            now.ToString(FileNameFormat, CultureInfo.InvariantCulture) + FileNameSuffix + ".zip");
        return WriteBackup(target, files, fingerprint);
    }

    internal BackupOutcome WriteBackup(string target, IReadOnlyList<(string Name, byte[] Content)> files, string fingerprint) {
        var folder = Path.GetDirectoryName(target)!;
        var tempFile = Path.Combine(folder, $"{TempFilePrefix}{Guid.NewGuid():N}{TempFileExtension}");
        try {
            using (var stream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write)) {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) {
                    zip.Comment = fingerprint;
                    foreach (var (name, content) in files) {
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                        entry.LastWriteTime = _timeProvider.GetLocalNow();
                        using var entryStream = entry.Open();
                        entryStream.Write(content);
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            // Never overwrites: if another machine wrote the same name meanwhile, its zip stays.
            if (File.Exists(target)) {
                _logger.LogInformation("Backup {File} already exists; keeping it", target);
                return BackupOutcome.NameTaken;
            }
            try {
                File.Move(tempFile, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target)) {
                _logger.LogInformation("Backup {File} already exists; keeping it", target);
                return BackupOutcome.NameTaken;
            }
            _logger.LogInformation("Backed up {Count} tagmark(s) to {File}", files.Count, target);
            return BackupOutcome.Created;
        }
        finally {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Deletes all but the newest <see cref="MaxBackups"/> backups among <paramref name="paths"/>.
    /// Files that don't look like backups are never touched.
    /// </summary>
    internal void Prune(IEnumerable<string> paths) {
        var backups = BackupFiles(paths);
        foreach (var path in backups.Take(Math.Max(0, backups.Count - MaxBackups))) {
            try {
                File.Delete(path);
                _logger.LogInformation("Deleted old backup {File}", path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
                // Another machine pruned it first.
            }
        }
    }

    // Oldest first: the names start with the timestamp, so ordinal order is time order.
    private static List<string> BackupFiles(IEnumerable<string> paths) =>
        paths.Where(p => BackupFileNameRegex().IsMatch(Path.GetFileName(p)))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

    private List<(string Name, byte[] Content)> ReadTagMarkFiles(string folder) {
        var files = new List<(string Name, byte[] Content)>();
        var names = Directory.EnumerateFiles(folder, $"*{_serializer.FileExtension}")
            .Where(p => p.EndsWith(_serializer.FileExtension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
        foreach (var path in names) {
            try {
                files.Add((Path.GetFileName(path), File.ReadAllBytes(path)));
            }
            catch (FileNotFoundException) {
                // Deleted since it was listed; the snapshot simply doesn't include it.
            }
        }
        return files;
    }

    private static string Fingerprint(IReadOnlyList<(string Name, byte[] Content)> files) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, content) in files) {
            hash.AppendData(Encoding.UTF8.GetBytes($"{name}\0{content.Length}\0"));
            hash.AppendData(content);
        }
        return FingerprintPrefix + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private string? ReadFingerprint(string zipPath) {
        try {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Comment;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) {
            _logger.LogWarning(ex, "Can't read backup {File}; making a new one", zipPath);
            return null;
        }
    }

    private void DeleteLeftoverTempFiles(string folder) {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - LeftoverTempFileAge;
        foreach (var path in Directory.EnumerateFiles(folder, $"{TempFilePrefix}*{TempFileExtension}")) {
            try {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch (IOException ex) {
                _logger.LogWarning(ex, "Can't delete leftover backup temp file {File}", path);
            }
        }
    }

    private static bool IsSameOrInside(string path, string folder) {
        var p = Path.TrimEndingDirectorySeparator(path);
        var f = Path.TrimEndingDirectorySeparator(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
