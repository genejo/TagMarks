namespace Cauldron.Models;

public sealed record TagMark(
    string Name,
    TagMarkType Type,
    string Payload,
    TagMarkMetadata Metadata,
    IReadOnlyList<string> SecondaryUris);
