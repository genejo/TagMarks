using Cauldron.Models;

namespace Cauldron.Managers;

public interface IConfigurationManager {
    ConfigurationData Config { get; }

    /// <summary>The config file read at startup.</summary>
    string ConfigFilePath { get; }

    /// <summary>True when the config file couldn't be parsed and the defaults are used for this run (spec §11 #55).</summary>
    bool IsConfigFileUnreadable { get; }

    /// <summary>Why the TagMarks folder couldn't be created, for the user; null when it's usable (spec §11 #53).</summary>
    string? TagMarksFolderError { get; }
}
