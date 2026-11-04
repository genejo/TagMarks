using Cauldron.Models;
using Cauldron.Services;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cauldron.Managers;

public class ConfigurationManager : IConfigurationManager
{
    public ConfigurationData Config { get; private set; } = new();
    public string ConfigFilePath => _configFilePath;
    public bool IsConfigFileUnreadable { get; private set; }
    public string? TagMarksFolderError { get; private set; }
    
    private readonly ILogger? _logger;
    private readonly string _configFilePath;
    private readonly Func<ConfigurationData> _createDefaults;
    
    private static readonly string DefaultConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
        "TagMarksConfig.yaml"
    );

    public ConfigurationManager(ILogger? logger = null)
        : this(DefaultConfigFilePath, () => new ConfigurationData(), logger) { }

    /// <summary>
    /// Creates a ConfigurationManager for another config file and defaults (for testing).
    /// </summary>
    public ConfigurationManager(string configFilePath, Func<ConfigurationData> createDefaults, ILogger? logger = null)
    {
        _configFilePath = configFilePath;
        _createDefaults = createDefaults;
        _logger = logger;
    }

    /// <summary>
    /// Creates a ConfigurationManager with an explicit config (for testing).
    /// </summary>
    public ConfigurationManager(ConfigurationData config)
    {
        Config = config;
        _configFilePath = DefaultConfigFilePath;
        _createDefaults = () => new ConfigurationData();
    }

    /// <summary>
    /// Loads the config file (spec §10.4, §11 #53–#55): a missing or empty file gets the defaults written to it,
    /// a broken one is left untouched while the defaults are used. Then makes sure the TagMarks folder exists.
    /// Both problems are reported through <see cref="IsConfigFileUnreadable"/> and <see cref="TagMarksFolderError"/>.
    /// </summary>
    public void InitializeConfiguration()
    {
        if (File.Exists(_configFilePath))
        {
            LoadConfiguration();
        }
        else
        {
            CreateDefaultConfiguration();
        }
        // YAML reads an empty value or a bare "~" as null.
        Config.TagMarksFolderPath = string.IsNullOrWhiteSpace(Config.TagMarksFolderPath)
            ? _createDefaults().TagMarksFolderPath
            : BackupService.ExpandHome(Config.TagMarksFolderPath.Trim());
        EnsureTagMarksFolder();
    }
    
    public static IDeserializer CreateDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .WithTypeConverter(new PerPlatformValueYamlConverter())
            .Build();

    public static ISerializer CreateSerializer() =>
        new SerializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .WithTypeConverter(new PerPlatformValueYamlConverter())
            .Build();
    
    private void LoadConfiguration()
    {
        try
        {
            var yamlContent = File.ReadAllText(_configFilePath);
            // An empty file, or one with only comments, deserializes to null.
            var config = CreateDeserializer().Deserialize<ConfigurationData?>(yamlContent);
            if (config is null)
            {
                _logger?.LogWarning("Configuration file {Path} is empty, writing defaults", _configFilePath);
                CreateDefaultConfiguration();
                return;
            }
            Config = config;
        }
        catch (Exception ex)
        {
            // Keep the user's file so the mistake can be fixed; the defaults apply only to this run.
            _logger?.LogError(ex, "Error loading configuration from {Path}, using defaults without saving them", _configFilePath);
            Config = _createDefaults();
            IsConfigFileUnreadable = true;
        }
    }
    
    private void CreateDefaultConfiguration() {
        Config = _createDefaults();
        SaveConfiguration();
    }

    private void EnsureTagMarksFolder()
    {
        try
        {
            Directory.CreateDirectory(Config.TagMarksFolderPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not create the TagMarks folder {Path}", Config.TagMarksFolderPath);
            TagMarksFolderError = $"Can't create the TagMarks folder {Config.TagMarksFolderPath}\n\n{ex.Message}\n\n"
                + $"The folder is set by TagMarksFolderPath in {_configFilePath}";
        }
    }

    private void SaveConfiguration()
    {
        try
        {
            var yamlContent = CreateSerializer().Serialize(Config);
            File.WriteAllText(_configFilePath, yamlContent);
            
            _logger?.LogInformation("Configuration saved at: {Path}", _configFilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error saving configuration");
        }
    }
}