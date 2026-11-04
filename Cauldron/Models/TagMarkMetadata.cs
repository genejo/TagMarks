namespace Cauldron.Models;

public sealed record TagMarkMetadata(
    DateTimeOffset LastOpened,
    DateTimeOffset Created,
    uint TimesOpened);
