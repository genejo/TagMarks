using Cauldron.Models;
using ReactiveUI.SourceGenerators;

namespace TagMarks.Desktop.ViewModels;

/// <summary>
/// The content of CONFIRM_PANEL (spec §3.4) and the operation it applies on Enter. Opening and
/// closing the panel is a state transition, which the owner makes.
/// </summary>
public sealed partial class ConfirmPanelViewModel : ViewModelBase {
    private const string UriIcon = "🔗";
    private const string ClipIcon = "📋";

    [Reactive] public partial string Message { get; private set; } = string.Empty;
    [Reactive] public partial string Uri { get; private set; } = string.Empty;
    [Reactive] public partial string CurrentLine { get; private set; } = string.Empty;
    [Reactive] public partial string NewLine { get; private set; } = string.Empty;
    [Reactive] public partial string ActionLabel { get; private set; } = string.Empty;

    /// <summary>
    /// What Enter applies, captured when the panel opens so an external change that moves the
    /// selection cannot redirect it to another tagmark. Null while the panel is empty.
    /// </summary>
    public PendingConfirmation? Pending { get; private set; }

    public void PrepareAddLink(string tagMark, bool fromExpanded, string uri) {
        Message = $"Add link to \"{tagMark}\"";
        Uri = uri;
        CurrentLine = string.Empty;
        NewLine = string.Empty;
        ActionLabel = "[Enter] Add   [Esc] Cancel";
        Pending = new PendingConfirmation(tagMark, fromExpanded, ConfirmKind.AddLink, uri, TagMarkType.Uri);
    }

    public void PrepareChangePayload(string tagMark, bool fromExpanded,
        string currentPayload, TagMarkType currentType, string newPayload, TagMarkType newType) {
        Message = $"Change payload for \"{tagMark}\"";
        Uri = string.Empty;
        CurrentLine = $"Current: {Icon(currentType)} {PayloadFormat.Truncate(PayloadFormat.Preview(currentPayload), 60)}";
        NewLine = $"New:     {Icon(newType)} {PayloadFormat.Truncate(PayloadFormat.Preview(newPayload), 60)}";
        ActionLabel = "[Enter] Update   [Esc] Cancel";
        Pending = new PendingConfirmation(tagMark, fromExpanded, ConfirmKind.ChangePayload, newPayload, newType);
    }

    public void Clear() {
        Message = string.Empty;
        Uri = string.Empty;
        CurrentLine = string.Empty;
        NewLine = string.Empty;
        ActionLabel = string.Empty;
        Pending = null;
    }

    private static string Icon(TagMarkType type) => type == TagMarkType.Uri ? UriIcon : ClipIcon;
}

public enum ConfirmKind { AddLink, ChangePayload }

/// <summary>
/// The operation CONFIRM_PANEL applies on Enter. Payload is the URI to add (AddLink)
/// or the new payload of type PayloadType (ChangePayload).
/// </summary>
public sealed record PendingConfirmation(string TagMark, bool FromExpanded, ConfirmKind Kind, string Payload, TagMarkType PayloadType);
