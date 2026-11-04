using Cauldron.Models;

namespace Cauldron.Services;

public interface ITagMarkUriService {
    public string GetUri(string name);
    public IReadOnlyCollection<string> GetTagMarks(string filter);
    /// <summary>Opens a URI-tagmark or records a clip activation; returns false only if the OS could not open the URI.</summary>
    public Task<bool> OpenTagMarkUriAsync(string name);
    /// <summary>Opens <paramref name="uri"/> on behalf of <paramref name="name"/>; returns false only if the OS could not open it.</summary>
    public Task<bool> OpenUriForTagMarkAsync(string name, string uri);
    public Task<bool> AddTagMarkAsync(string name, string uri);
    public Task<bool> AddClipTagMarkAsync(string name, string clipText);
    /// <summary>Rescans the folder; raises <see cref="TagMarksChanged"/> when data changed.</summary>
    public Task RefreshTagMarksAsync();
    IComparer<string> TagMarkComparer { get; }
    bool TagMarkExists(string name);
    string? FindTagMarkByUri(string uri);
    /// <summary>
    /// URI-tagmarks whose primary URI another tagmark owns (files edited outside the app), each mapped to
    /// that owner. The owner is the name that sorts first, case-insensitive (spec §5.1, §11 #50).
    /// </summary>
    IReadOnlyDictionary<string, string> DuplicateUriOwners { get; }
    
    Task<bool> MoveToTrashAsync(string name);
    Task<bool> RestoreFromTrashAsync(string name);
    void PermanentlyDeleteFromTrash(string name);
    Task<bool> RenameTagMarkAsync(string oldName, string newName);
    int StartupRecoveryCount { get; }
    
    IReadOnlyList<string> GetSecondaryUris(string name);
    Task<bool> AddSecondaryUriAsync(string name, string uri);
    Task<bool> RemoveSecondaryUriAsync(string name, string uri);
    /// <summary>Re-inserts a removed link at <paramref name="index"/> (clamped); never adds a reverse link.</summary>
    Task<bool> RestoreSecondaryUriAsync(string name, string uri, int index);
    
    TagMarkType GetTagMarkType(string name);
    TagMarkPayload GetPayload(string name);
    TagMarkInfo GetTagMarkInfo(string name);
    Task<bool> ChangePayloadAsync(string name, string newPayload, TagMarkType newType);
    
    event Action? TagMarksChanged;
}
