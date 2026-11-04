using Cauldron.Managers;
using Cauldron.Models;
using Microsoft.Extensions.Logging;

namespace Cauldron.Services;

public class TagMarkUriService : ITagMarkUriService, IDisposable {
    private readonly string _folderPath;
    private readonly string _trashFolderPath;
    private readonly ILogger<TagMarkUriService> _logger;
    private readonly ITagMarkSerializer _serializer;
    private readonly IUriLauncher _uriLauncher;

    private volatile CacheState _cache = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;

    public const int FileWatcherDebounceMs = 200;
    public const string TempFileExtension = ".tmp";

    // Each snapshot owns its comparer, so a sort in progress never sees a cache swap.
    public IComparer<string> TagMarkComparer => _cache.Comparer;
    public int StartupRecoveryCount { get; private set; }
    public event Action? TagMarksChanged;

    public TagMarkUriService(IConfigurationManager configurationManager, ILogger<TagMarkUriService> logger,
        ITagMarkSerializer serializer, IUriLauncher uriLauncher) {
        _logger = logger;
        _serializer = serializer;
        _uriLauncher = uriLauncher;
        _folderPath = configurationManager.Config.TagMarksFolderPath;
        _trashFolderPath = Path.Combine(_folderPath, "_trash");
        if (!Directory.Exists(_folderPath))
            throw new DirectoryNotFoundException(_folderPath);
        DeleteLeftoverTempFiles();
        StartupRecoveryCount = RecoverFromTrash();
        if (StartupRecoveryCount > 0)
            _logger.LogInformation("Recovered {Count} tagmark(s) from interrupted deletion.", StartupRecoveryCount);
        // Task.Run escapes the caller's SynchronizationContext so the async continuations
        // resume on thread pool threads, avoiding a deadlock when blocking with GetResult().
        Task.Run(RefreshCoreAsync).GetAwaiter().GetResult();

        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(_folderPath, $"*{_serializer.FileExtension}") {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Changed += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.EnableRaisingEvents = true;
    }

    public string GetUri(string name) => GetPrimaryUri(_cache, name) ?? string.Empty;

    public IReadOnlyCollection<string> GetTagMarks(string filter) {
        var cache = _cache;
        return (filter != string.Empty ? cache.Seeker.Query(filter) : cache.TypeCache.Keys)
            .ToList()
            .AsReadOnly();
    }

    public async Task<bool> OpenTagMarkUriAsync(string name) {
        var cache = _cache;
        if (cache.TypeCache.TryGetValue(name, out var type) && type == TagMarkType.Clip) {
            await UpdateMetadataFromFileAsync(name);
            return true;
        }
        // A duplicate (spec §11 #50) isn't in the bidirectional map but still opens its own URI.
        if (GetPrimaryUri(cache, name) is not { } uri) return true;
        return await OpenUriForTagMarkAsync(name, uri);
    }

    public async Task<bool> OpenUriForTagMarkAsync(string name, string uri) {
        var cache = _cache;
        if (!cache.TypeCache.ContainsKey(name)) return true;
        // A URI the OS could not open doesn't count as opened.
        if (!await _uriLauncher.LaunchAsync(uri)) return false;
        await UpdateMetadataFromFileAsync(name);
        return true;
    }

    public async Task<bool> AddTagMarkAsync(string name, string uri) {
        if (!Validation.IsValidUri(uri)) {
            _logger.LogWarning("Rejected invalid URI for TagMark {Name}: {Uri}", name, uri);
            return false;
        }
        if (TagMarkExists(name)) {
            _logger.LogWarning("Rejected duplicate name for TagMark {Name}", name);
            return false;
        }
        if (FindTagMarkByUri(uri) is not null) {
            _logger.LogWarning("Rejected duplicate URI {Uri} for TagMark {Name}", uri, name);
            return false;
        }
        var fullFilePath = GetFileNameFromTagMark(name);
        try {
            var entry = new TagMark(
                name,
                TagMarkType.Uri,
                uri,
                new TagMarkMetadata(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, 0),
                []);
            await WriteTagMarkAsync(fullFilePath, entry, overwrite: false);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error adding TagMark {Name} for URI {Uri}", name, uri);
            return false;
        }
        await RefreshEntriesAsync(name);
        return true;
    }

    public async Task<bool> AddClipTagMarkAsync(string name, string clipText) {
        if (string.IsNullOrWhiteSpace(clipText)) {
            _logger.LogWarning("Rejected empty clip text for TagMark {Name}", name);
            return false;
        }
        if (Validation.IsClipPayloadTooLong(clipText)) {
            _logger.LogWarning("Rejected clip text exceeding {Max} chars for TagMark {Name}", Validation.MaxClipPayloadLength, name);
            return false;
        }
        if (TagMarkExists(name)) {
            _logger.LogWarning("Rejected duplicate name for clip TagMark {Name}", name);
            return false;
        }
        var fullFilePath = GetFileNameFromTagMark(name);
        try {
            var entry = new TagMark(
                name,
                TagMarkType.Clip,
                clipText,
                new TagMarkMetadata(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, 0),
                []);
            await WriteTagMarkAsync(fullFilePath, entry, overwrite: false);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error adding clip TagMark {Name}", name);
            return false;
        }
        await RefreshEntriesAsync(name);
        return true;
    }

    public async Task<bool> MoveToTrashAsync(string name) {
        var cache = _cache;
        if (!cache.TypeCache.ContainsKey(name)) return false;

        var sourceFile = GetFileNameFromTagMark(name);
        var trashFile = GetTrashFileNameFromTagMark(name);
        try {
            Directory.CreateDirectory(_trashFolderPath);
            // An older copy with this name is still pending deletion; it can't be restored once this one is back (spec §4.2).
            PermanentlyDeleteFromTrash(name);
            File.Move(sourceFile, trashFile);
            await RefreshEntriesAsync(name);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error moving TagMark {Name} to trash", name);
            return false;
        }
        return true;
    }

    public async Task<bool> RestoreFromTrashAsync(string name) {
        var trashFile = GetTrashFileNameFromTagMark(name);
        var destFile = GetFileNameFromTagMark(name);
        try {
            if (!File.Exists(trashFile)) return false;
            File.Move(trashFile, destFile);
            await RefreshEntriesAsync(name);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error restoring TagMark {Name} from trash", name);
            return false;
        }
        return true;
    }

    public void PermanentlyDeleteFromTrash(string name) {
        var trashFile = GetTrashFileNameFromTagMark(name);
        try {
            if (File.Exists(trashFile)) {
                string? deletedPrimaryUri = TryReadPrimaryUriFromFile(trashFile, name);
                File.Delete(trashFile);
                // Another tagmark may own the URI again (re-created during the undo window).
                if (!string.IsNullOrEmpty(deletedPrimaryUri) && FindTagMarkByUri(deletedPrimaryUri) is null)
                    Task.Run(() => PruneDanglingLinksAsync(deletedPrimaryUri)).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error permanently deleting TagMark {Name} from trash", name);
        }
    }

    public async Task<bool> RenameTagMarkAsync(string oldName, string newName) {
        var cache = _cache;
        if (!cache.TypeCache.ContainsKey(oldName)) return false;

        var sourceFile = GetFileNameFromTagMark(oldName);
        var destFile = GetFileNameFromTagMark(newName);
        try {
            File.Move(sourceFile, destFile);
            await RefreshEntriesAsync(oldName, newName);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error renaming TagMark {OldName} to {NewName}", oldName, newName);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Removes temp files left by a write interrupted by a crash or power loss. The tagmark
    /// file itself is replaced only by the final rename, so it is still intact.
    /// </summary>
    private void DeleteLeftoverTempFiles() {
        foreach (var tempFile in Directory.EnumerateFiles(_folderPath, $"*{_serializer.FileExtension}.*{TempFileExtension}")) {
            try {
                File.Delete(tempFile);
                _logger.LogInformation("Deleted leftover temp file {File}", Path.GetFileName(tempFile));
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error deleting leftover temp file {File}", Path.GetFileName(tempFile));
            }
        }
    }

    private int RecoverFromTrash() {
        if (!Directory.Exists(_trashFolderPath)) return 0;
        int recovered = 0;
        foreach (var trashFile in Directory.EnumerateFiles(_trashFolderPath, $"*{_serializer.FileExtension}")) {
            var fileName = Path.GetFileName(trashFile);
            var destFile = Path.Combine(_folderPath, fileName);
            try {
                File.Move(trashFile, destFile, overwrite: false);
                recovered++;
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error recovering {File} from trash", fileName);
            }
        }
        return recovered;
    }

    /// <summary>Rescans the folder off the caller's thread; raises <see cref="TagMarksChanged"/> when data changed.</summary>
    public async Task RefreshTagMarksAsync() {
        if (await Task.Run(RefreshCoreAsync))
            TagMarksChanged?.Invoke();
    }

    /// <summary>
    /// Reloads all data maps from disk. Returns true when any observable data changed
    /// (names, URIs, types, payloads, metadata, or secondary URIs).
    /// </summary>
    private async Task<bool> RefreshCoreAsync() {
        await _refreshLock.WaitAsync();
        try {
            var prev = _cache;
            await PopulateDataMapsAsync();
            var curr = _cache;
            LogNewDuplicates(prev, curr);
            return HasDataChanged(prev, curr);
        }
        finally {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Re-reads only the files of <paramref name="names"/> and swaps in a cache built from the
    /// current one, so a write costs O(1) disk reads. Full rescans stay with the file watcher.
    /// </summary>
    private async Task RefreshEntriesAsync(params string[] names) {
        await _refreshLock.WaitAsync();
        try {
            var builder = CacheBuilder.From(_cache);
            foreach (var name in names)
                builder.Remove(name);
            foreach (var filePath in FindTagMarkFiles(names)) {
                try {
                    var name = Path.GetFileNameWithoutExtension(filePath);
                    // The on-disk spelling may name a different cached entry than the one requested.
                    builder.Remove(name);
                    var tagMark = await ReadTagMarkAsync(filePath, name);
                    builder.Add(tagMark);
                }
                catch (Exception ex) {
                    _logger.LogError(ex, "Error refreshing data maps for {FilePath}", filePath);
                }
            }
            var prev = _cache;
            _cache = builder.Build();
            LogNewDuplicates(prev, _cache);
        }
        finally {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Returns the files of <paramref name="names"/> with their on-disk spelling. On a
    /// case-insensitive file system a case-only rename leaves one file for both names.
    /// </summary>
    private IEnumerable<string> FindTagMarkFiles(IEnumerable<string> names) {
        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, MatchType = MatchType.Simple };
        return names
            .SelectMany(name => Directory.EnumerateFiles(_folderPath, $"{name}{_serializer.FileExtension}", options)
                .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase)))
            .Distinct();
    }

    private void LogNewDuplicates(CacheState prev, CacheState curr) {
        foreach (var (name, owner) in curr.DuplicateUriOwners)
            if (!prev.DuplicateUriOwners.ContainsKey(name))
                _logger.LogWarning("Duplicate URI {Uri} in {Existing} and {New}",
                    curr.PayloadCache[name], owner, name);
    }

    private static bool HasDataChanged(CacheState prev, CacheState curr) {
        if (!new HashSet<string>(prev.BiMapTagMarkUri.Keys).SetEquals(curr.BiMapTagMarkUri.Keys))
            return true;
        foreach (var name in curr.BiMapTagMarkUri.Keys)
            if (!prev.BiMapTagMarkUri.TryGetValue(name, out var uri) || uri != curr.BiMapTagMarkUri[name])
                return true;
        if (prev.PayloadCache.Count != curr.PayloadCache.Count)
            return true;
        foreach (var (name, payload) in curr.PayloadCache)
            if (!prev.PayloadCache.TryGetValue(name, out var p) || p != payload)
                return true;
        if (prev.TypeCache.Count != curr.TypeCache.Count)
            return true;
        foreach (var (name, type) in curr.TypeCache)
            if (!prev.TypeCache.TryGetValue(name, out var t) || t != type)
                return true;
        // LastOpened drives the sort order.
        if (prev.MetadataByName.Count != curr.MetadataByName.Count)
            return true;
        foreach (var (name, meta) in curr.MetadataByName)
            if (!prev.MetadataByName.TryGetValue(name, out var m) || m != meta)
                return true;
        if (prev.SecondaryUris.Count != curr.SecondaryUris.Count)
            return true;
        foreach (var (name, uris) in curr.SecondaryUris)
            if (!prev.SecondaryUris.TryGetValue(name, out var u) || !u.SequenceEqual(uris))
                return true;
        return false;
    }

    public bool TagMarkExists(string name) {
        var cache = _cache;
        return cache.TypeCache.ContainsKey(name);
    }

    public IReadOnlyDictionary<string, string> DuplicateUriOwners => _cache.DuplicateUriOwners;

    public string? FindTagMarkByUri(string uri) {
        var cache = _cache;
        return cache.BiMapTagMarkUri.ContainsValue(uri) ? cache.BiMapTagMarkUri.Inverse[uri] : null;
    }

    public IReadOnlyList<string> GetSecondaryUris(string name) {
        var cache = _cache;
        return cache.SecondaryUris.TryGetValue(name, out var uris) ? uris.AsReadOnly() : [];
    }

    public async Task<bool> AddSecondaryUriAsync(string name, string uri) {
        var cache = _cache;
        
        if (GetPrimaryUri(cache, name) is { } primaryUri) {
            // URI tagmark: existing behavior
            if (primaryUri == uri) return false;

            var existing = cache.SecondaryUris.GetValueOrDefault(name, []);
            if (existing.Contains(uri)) return false;

            if (!await PersistAddSecondaryUriAsync(name, uri)) return false;

            // Bidirectional: if URI matches another tagmark's primary, add reverse link
            if (cache.BiMapTagMarkUri.ContainsValue(uri)) {
                string otherName = cache.BiMapTagMarkUri.Inverse[uri];
                var otherLinks = cache.SecondaryUris.GetValueOrDefault(otherName, []);
                if (!otherLinks.Contains(primaryUri)) {
                    await PersistAddSecondaryUriAsync(otherName, primaryUri);
                    await RefreshEntriesAsync(name, otherName);
                    return true;
                }
            }

            await RefreshEntriesAsync(name);
            return true;
        }

        // Clip tagmark: allow secondary URIs but reject if URI matches the clip payload
        // (prevents primary/secondary collision if the user later switches to URI type).
        if (cache.TypeCache.TryGetValue(name, out var type) && type == TagMarkType.Clip) {
            if (cache.PayloadCache.TryGetValue(name, out var payload) && payload == uri)
                return false;

            var existing = cache.SecondaryUris.GetValueOrDefault(name, []);
            if (existing.Contains(uri)) return false;

            if (!await PersistAddSecondaryUriAsync(name, uri)) return false;
            await RefreshEntriesAsync(name);
            return true;
        }

        return false;
    }

    public async Task<bool> RemoveSecondaryUriAsync(string name, string uri) {
        var cache = _cache;
        if (!cache.SecondaryUris.TryGetValue(name, out var uris)) return false;
        if (!uris.Contains(uri)) return false;
        if (!await PersistRemoveSecondaryUriAsync(name, uri)) return false;
        await RefreshEntriesAsync(name);
        return true;
    }

    public async Task<bool> RestoreSecondaryUriAsync(string name, string uri, int index) {
        var cache = _cache;
        if (!cache.TypeCache.ContainsKey(name)) return false;
        if (GetPayload(cache, name).Value == uri) return false;
        if (cache.SecondaryUris.GetValueOrDefault(name, []).Contains(uri)) return false;

        // Unlike AddSecondaryUriAsync, no reverse link: removing the link didn't remove it either.
        if (!await PersistAddSecondaryUriAsync(name, uri, index)) return false;
        await RefreshEntriesAsync(name);
        return true;
    }

    public TagMarkType GetTagMarkType(string name) {
        var cache = _cache;
        return cache.TypeCache.TryGetValue(name, out var type) ? type : TagMarkType.Uri;
    }

    public TagMarkPayload GetPayload(string name) => GetPayload(_cache, name);

    public TagMarkInfo GetTagMarkInfo(string name) {
        var cache = _cache;
        return new TagMarkInfo(GetPrimaryUri(cache, name) ?? string.Empty, GetPayload(cache, name));
    }

    /// <summary>
    /// The primary URI of a URI-tagmark, or null for a clip or unknown name. Read from the payload, so a
    /// duplicate that doesn't own its URI in the bidirectional map still has one (spec §11 #50).
    /// </summary>
    private static string? GetPrimaryUri(CacheState cache, string name) =>
        cache.TypeCache.TryGetValue(name, out var type) && type == TagMarkType.Uri
            ? cache.PayloadCache[name]
            : null;

    private static TagMarkPayload GetPayload(CacheState cache, string name)
    {
        var type = cache.TypeCache.TryGetValue(name, out var t) ? t : TagMarkType.Uri;
        var value = cache.PayloadCache.TryGetValue(name, out var payload) ? payload
            : cache.BiMapTagMarkUri.TryGetValue(name, out var uri) ? uri : string.Empty;
        return type == TagMarkType.Clip
            ? new TagMarkPayload.Clip(value)
            : new TagMarkPayload.Uri(value);
    }

    public async Task<bool> ChangePayloadAsync(string name, string newPayload, TagMarkType newType) {
        if (newType == TagMarkType.Uri && !Validation.IsValidUri(newPayload)) {
            _logger.LogWarning("Rejected invalid URI payload for TagMark {Name}", name);
            return false;
        }
        if (newType == TagMarkType.Uri) {
            var existingOwner = FindTagMarkByUri(newPayload);
            if (existingOwner is not null && existingOwner != name) {
                _logger.LogWarning("Rejected duplicate URI {Uri} for TagMark {Name} (owned by {Existing})", newPayload, name, existingOwner);
                return false;
            }
        }
        if (newType == TagMarkType.Clip) {
            if (string.IsNullOrWhiteSpace(newPayload)) {
                _logger.LogWarning("Rejected empty clip payload for TagMark {Name}", name);
                return false;
            }
            if (Validation.IsClipPayloadTooLong(newPayload)) {
                _logger.LogWarning("Rejected clip payload exceeding {Max} chars for TagMark {Name}", Validation.MaxClipPayloadLength, name);
                return false;
            }
        }
        var fullFilePath = GetFileNameFromTagMark(name);
        try {
            var entry = await ReadTagMarkAsync(fullFilePath, name);
            var updated = entry with { Payload = newPayload, Type = newType };
            await WriteTagMarkAsync(fullFilePath, updated);
            await RefreshEntriesAsync(name);
            return true;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error changing payload for TagMark {Name}", name);
            return false;
        }
    }

    private async Task<bool> PersistAddSecondaryUriAsync(string name, string uri, int? index = null) {
        var fullFilePath = GetFileNameFromTagMark(name);
        try {
            var entry = await ReadTagMarkAsync(fullFilePath, name);
            var updatedUris = new List<string>(entry.SecondaryUris);
            updatedUris.Insert(Math.Clamp(index ?? updatedUris.Count, 0, updatedUris.Count), uri);
            var updated = entry with { SecondaryUris = updatedUris };
            await WriteTagMarkAsync(fullFilePath, updated);
            return true;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error adding secondary URI {Uri} to {Name}", uri, name);
            return false;
        }
    }

    private async Task<bool> PersistRemoveSecondaryUriAsync(string name, string uri) {
        var fullFilePath = GetFileNameFromTagMark(name);
        try {
            var entry = await ReadTagMarkAsync(fullFilePath, name);
            var updatedUris = new List<string>(entry.SecondaryUris);
            updatedUris.Remove(uri);
            var updated = entry with { SecondaryUris = updatedUris };
            await WriteTagMarkAsync(fullFilePath, updated);
            return true;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error removing secondary URI {Uri} from {Name}", uri, name);
            return false;
        }
    }

    private async Task UpdateMetadataFromFileAsync(string name) {
        var fullFilePath = GetFileNameFromTagMark(name);
        try {
            var entry = await ReadTagMarkAsync(fullFilePath, name);
            var meta = entry.Metadata;
            var updatedMeta = new TagMarkMetadata(DateTimeOffset.Now, meta.Created, meta.TimesOpened + 1);
            var updated = entry with { Metadata = updatedMeta };
            await WriteTagMarkAsync(fullFilePath, updated);
            await RefreshEntriesAsync(name);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error updating metadata for TagMark {Name}", name);
        }
    }

    private string GetFileNameFromTagMark(string name) {
        string result = Path.Combine(_folderPath, $"{name}{_serializer.FileExtension}");
        return result;
    }

    private string GetTrashFileNameFromTagMark(string name) {
        return Path.Combine(_trashFolderPath, $"{name}{_serializer.FileExtension}");
    }

    private async Task<TagMark> ReadTagMarkAsync(string fullFilePath, string name) {
        await using var readStream = new FileStream(fullFilePath, FileMode.Open, FileAccess.Read);
        return await _serializer.DeserializeAsync(readStream, name);
    }

    /// <summary>
    /// Writes to a temp file in the same folder, then renames it over the tagmark file, so a crash,
    /// power loss or a sync client reading mid-write never sees an empty or partial tagmark.
    /// The temp file ends in <see cref="TempFileExtension"/>, so the watcher and loading ignore it.
    /// With <paramref name="overwrite"/> false, the rename fails if the tagmark file already exists.
    /// </summary>
    private async Task WriteTagMarkAsync(string fullFilePath, TagMark entry, bool overwrite = true) {
        var tempFilePath = $"{fullFilePath}.{Guid.NewGuid():N}{TempFileExtension}";
        try {
            await using (var writeStream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write)) {
                await _serializer.SerializeAsync(entry, writeStream);
                writeStream.Flush(flushToDisk: true);
            }
            File.Move(tempFilePath, fullFilePath, overwrite);
        }
        catch {
            try {
                File.Delete(tempFilePath);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error deleting temp file {File}", tempFilePath);
            }
            throw;
        }
    }

    private string? TryReadPrimaryUriFromFile(string fullFilePath, string name) {
        try {
            var entry = Task.Run(() => ReadTagMarkAsync(fullFilePath, name)).GetAwaiter().GetResult();
            return entry.Type == TagMarkType.Uri ? entry.Payload : null;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error reading trashed TagMark {Name}", name);
            return null;
        }
    }

    private async Task PruneDanglingLinksAsync(string deletedPrimaryUri) {
        var prunedNames = new List<string>();
        foreach (var filePath in Directory.EnumerateFiles(_folderPath, $"*{_serializer.FileExtension}")) {
            var name = Path.GetFileNameWithoutExtension(filePath);
            try {
                var entry = await ReadTagMarkAsync(filePath, name);
                if (!entry.SecondaryUris.Contains(deletedPrimaryUri)) continue;

                var updatedUris = entry.SecondaryUris
                    .Where(uri => uri != deletedPrimaryUri)
                    .ToList();
                var updated = entry with { SecondaryUris = updatedUris };
                await WriteTagMarkAsync(filePath, updated);
                prunedNames.Add(name);

                _logger.LogInformation("Pruned dangling secondary URI {Uri} from {Name}", deletedPrimaryUri, entry.Name);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error pruning dangling secondary URI {Uri} from {Name}", deletedPrimaryUri, name);
            }
        }
        await RefreshEntriesAsync([.. prunedNames]);
    }

    private async Task PopulateDataMapsAsync() {
        var builder = new CacheBuilder();

        foreach (var filePath in Directory.EnumerateFiles(_folderPath, $"*{_serializer.FileExtension}")) {
            try {
                var name = Path.GetFileNameWithoutExtension(filePath);
                var tagMark = await ReadTagMarkAsync(filePath, name);
                builder.Add(tagMark);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Error populating data maps for {FilePath}", filePath);
            }
        }

        // Atomic swap — readers see either the old complete set or the new complete set,
        // never a partially-cleared intermediate state.
        _cache = builder.Build();
    }

    // ── FileSystemWatcher ────────────────────────────────────────

    private void OnFileChanged(object sender, FileSystemEventArgs e) =>
        _debounceTimer.Change(FileWatcherDebounceMs, Timeout.Infinite);

    private void OnFileRenamed(object sender, RenamedEventArgs e) =>
        _debounceTimer.Change(FileWatcherDebounceMs, Timeout.Infinite);

    // Timer callback — async void is intentional; errors are caught below.
    private async void OnDebounceElapsed(object? state) {
        try {
            await RefreshTagMarksAsync();
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error during file-watcher refresh");
        }
    }

    public void Dispose() {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounceTimer.Dispose();
    }

    /// <summary>Mutable maps from which an immutable <see cref="CacheState"/> snapshot is built.</summary>
    private class CacheBuilder {
        private readonly BidirectionalDictionary<string, string> _biMap = new();
        private readonly Dictionary<string, TagMarkMetadata> _uriMeta = new();
        private readonly Dictionary<string, TagMarkMetadata> _nameMeta = new();
        private readonly Dictionary<string, List<string>> _secondary = new();
        private readonly Dictionary<string, TagMarkType> _types = new();
        private readonly Dictionary<string, string> _payloads = new();

        // Among tagmarks sharing a primary URI, the name that sorts first owns it (spec §5.1). The ordinal
        // tie-break keeps the owner deterministic for names differing only by case.
        private static readonly IComparer<string> OwnerOrder = Comparer<string>.Create((a, b) => {
            int byName = StringComparer.OrdinalIgnoreCase.Compare(a, b);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(a, b);
        });

        public static CacheBuilder From(CacheState cache) {
            var builder = new CacheBuilder();
            foreach (var (name, type) in cache.TypeCache)
                builder.Add(new TagMark(
                    name,
                    type,
                    cache.PayloadCache[name],
                    cache.MetadataByName[name],
                    cache.SecondaryUris.GetValueOrDefault(name, [])));
            return builder;
        }

        /// <summary>
        /// Adds <paramref name="tagMark"/>. If another URI-tagmark has the same primary URI, the one whose
        /// name sorts first owns it in the bidirectional map, whatever the order they were added in.
        /// </summary>
        public void Add(TagMark tagMark) {
            // Currently only URI-tagmarks are tracked in the bidirectional map
            if (tagMark.Type == TagMarkType.Uri) {
                if (!_biMap.ContainsValue(tagMark.Payload))
                    RegisterOwner(tagMark.Name, tagMark.Payload, tagMark.Metadata);
                else if (OwnerOrder.Compare(tagMark.Name, _biMap.Inverse[tagMark.Payload]) < 0) {
                    UnregisterOwner(_biMap.Inverse[tagMark.Payload]);
                    RegisterOwner(tagMark.Name, tagMark.Payload, tagMark.Metadata);
                }
            }

            _types[tagMark.Name] = tagMark.Type;
            _payloads[tagMark.Name] = tagMark.Payload;
            _nameMeta[tagMark.Name] = tagMark.Metadata;

            // Store secondary URIs for all tagmarks (URI and clip)
            if (tagMark.SecondaryUris.Count > 0)
                _secondary[tagMark.Name] = new List<string>(tagMark.SecondaryUris);
        }

        public void Remove(string name) {
            var ownedUri = _biMap.TryGetValue(name, out var uri) ? uri : null;
            if (ownedUri is not null)
                UnregisterOwner(name);
            _types.Remove(name);
            _payloads.Remove(name);
            _nameMeta.Remove(name);
            _secondary.Remove(name);
            // A duplicate of the removed owner takes over its URI.
            if (ownedUri is not null && NamesWithPrimaryUri(ownedUri).Min(OwnerOrder) is { } next)
                RegisterOwner(next, ownedUri, _nameMeta[next]);
        }

        private void RegisterOwner(string name, string uri, TagMarkMetadata metadata) {
            _biMap.Add(name, uri);
            _uriMeta.Add(uri, metadata);
        }

        private void UnregisterOwner(string name) {
            _uriMeta.Remove(_biMap[name]);
            _biMap.Remove(name);
        }

        private IEnumerable<string> NamesWithPrimaryUri(string uri) =>
            _types.Where(e => e.Value == TagMarkType.Uri && _payloads[e.Key] == uri).Select(e => e.Key);

        public CacheState Build() => new() {
            BiMapTagMarkUri = _biMap,
            MapUriMetadata = _uriMeta,
            MetadataByName = _nameMeta,
            SecondaryUris = _secondary,
            TypeCache = _types,
            PayloadCache = _payloads,
            DuplicateUriOwners = _types
                .Where(e => e.Value == TagMarkType.Uri && !_biMap.ContainsKey(e.Key))
                .ToDictionary(e => e.Key, e => _biMap.Inverse[_payloads[e.Key]]),
            Seeker = new SubstringSearchService(_types.Keys)
        };
    }

    private class CacheState {
        public BidirectionalDictionary<string, string> BiMapTagMarkUri { get; init; } = new();
        public Dictionary<string, TagMarkMetadata> MapUriMetadata { get; init; } = new();
        public Dictionary<string, TagMarkMetadata> MetadataByName { get; init; } = new();
        public Dictionary<string, List<string>> SecondaryUris { get; init; } = new();
        public Dictionary<string, TagMarkType> TypeCache { get; init; } = new();
        public Dictionary<string, string> PayloadCache { get; init; } = new();
        // Non-owner name → owner name, for URI-tagmarks sharing a primary URI (spec §11 #50).
        public Dictionary<string, string> DuplicateUriOwners { get; init; } = new();
        public SubstringSearchService Seeker { get; init; } = new([]);

        // Most recently opened first, then alphabetically (case-insensitive) for ties;
        // the ordinal tie-break keeps the order total for names differing only by case.
        public IComparer<string> Comparer { get; }

        public CacheState() {
            Comparer = Comparer<string>.Create((a, b) => {
                int byDate = GetLastOpened(b).CompareTo(GetLastOpened(a));
                if (byDate != 0) return byDate;
                int byName = StringComparer.OrdinalIgnoreCase.Compare(a, b);
                return byName != 0 ? byName : StringComparer.Ordinal.Compare(a, b);
            });
        }

        private DateTimeOffset GetLastOpened(string name) =>
            MetadataByName.TryGetValue(name, out var meta) ? meta.LastOpened : DateTimeOffset.MinValue;
    }
}
