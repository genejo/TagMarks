using Cauldron.Models;

namespace Cauldron.Services;

public interface ITagMarkSerializer
{
    string FileExtension { get; }
    Task<TagMark> DeserializeAsync(Stream stream, string name);
    Task SerializeAsync(TagMark tagMark, Stream stream);
}
