using Cauldron.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Cauldron.Services;

/// <summary>
/// Reads a <see cref="PerPlatformValue"/> from a plain scalar (every platform) or a <c>Mac</c>/<c>Windows</c>/<c>Linux</c>
/// mapping, and writes it back as a scalar when all platforms share the value.
/// </summary>
public sealed class PerPlatformValueYamlConverter : IYamlTypeConverter {
    public bool Accepts(Type type) => type == typeof(PerPlatformValue);

    /// <summary>
    /// Never throws on a wrongly shaped value: the bad parts are skipped and described in
    /// <see cref="PerPlatformValue.Error"/>, and the valid platforms are kept.
    /// </summary>
    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) {
        if (parser.TryConsume<Scalar>(out var scalar))
            return PerPlatformValue.ForAll(scalar.Value);

        if (!parser.TryConsume<MappingStart>(out _)) {
            parser.SkipThisAndNestedEvents();
            return new PerPlatformValue { Error = "expected a value or a Mac/Windows/Linux mapping" };
        }

        var value = new PerPlatformValue();
        var errors = new List<string>();
        while (!parser.TryConsume<MappingEnd>(out _)) {
            if (!parser.TryConsume<Scalar>(out var key)) {
                parser.SkipThisAndNestedEvents();
                parser.SkipThisAndNestedEvents();
                errors.Add("a platform name must be Mac, Windows or Linux");
                continue;
            }
            if (!parser.TryConsume<Scalar>(out var platformValue)) {
                parser.SkipThisAndNestedEvents();
                errors.Add($"the {key.Value} value must be a single value, not a list or mapping");
                continue;
            }
            if (key.Value.Equals(nameof(PerPlatformValue.Mac), StringComparison.OrdinalIgnoreCase))
                value.Mac = platformValue.Value;
            else if (key.Value.Equals(nameof(PerPlatformValue.Windows), StringComparison.OrdinalIgnoreCase))
                value.Windows = platformValue.Value;
            else if (key.Value.Equals(nameof(PerPlatformValue.Linux), StringComparison.OrdinalIgnoreCase))
                value.Linux = platformValue.Value;
            else
                errors.Add($"unknown platform '{key.Value}' (use Mac, Windows or Linux)");
        }
        if (errors.Count > 0)
            value.Error = string.Join("; ", errors);
        return value;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) {
        var platformValue = (PerPlatformValue?)value;
        if (platformValue is null) {
            emitter.Emit(new Scalar(string.Empty));
            return;
        }
        if (platformValue.Mac == platformValue.Windows && platformValue.Windows == platformValue.Linux) {
            emitter.Emit(new Scalar(platformValue.Mac ?? string.Empty));
            return;
        }

        emitter.Emit(new MappingStart());
        Emit(nameof(PerPlatformValue.Mac), platformValue.Mac);
        Emit(nameof(PerPlatformValue.Windows), platformValue.Windows);
        Emit(nameof(PerPlatformValue.Linux), platformValue.Linux);
        emitter.Emit(new MappingEnd());

        void Emit(string key, string? platform) {
            if (platform is null)
                return;
            emitter.Emit(new Scalar(key));
            emitter.Emit(new Scalar(platform));
        }
    }
}
