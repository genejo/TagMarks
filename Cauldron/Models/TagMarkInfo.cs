namespace Cauldron.Models;

/// <summary>
/// The selected-item view of a tagmark, read from one cache snapshot so the fields
/// always agree with each other. <see cref="Uri"/> is empty for clip tagmarks.
/// </summary>
public sealed record TagMarkInfo(string Uri, TagMarkPayload Payload)
{
    public TagMarkType Type => Payload is TagMarkPayload.Clip ? TagMarkType.Clip : TagMarkType.Uri;
}
