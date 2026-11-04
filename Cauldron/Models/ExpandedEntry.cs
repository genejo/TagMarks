namespace Cauldron.Models;

/// <summary>
/// Represents a single entry in the expanded view of a tagmark.
/// Can be the primary URI or a secondary URI link.
/// </summary>
public sealed record ExpandedEntry(
    string Uri,
    string DisplayText,
    bool IsPrimary,
    string? LinkedTagMarkName);
