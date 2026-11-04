namespace Cauldron.Models;

public abstract record TagMarkPayload(string Value)
{
    public sealed record Uri(string Value) : TagMarkPayload(Value);
    public sealed record Clip(string Value) : TagMarkPayload(Value);
}
