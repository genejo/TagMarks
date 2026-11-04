using System.Text;
using Cauldron.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.Converters;

namespace Cauldron.Services;

public class BatTagMarkSerializer : ITagMarkSerializer
{
    public string FileExtension => ".bat";

    private const string RemPrefix = "rem ";
    private const string RemLinkPrefix = "rem Link: ";
    private const string StartPrefix = "start \"\" \"";
    private const string EchoOff = "@echo off";
    private const string GotoEof = "goto :eof";
    private const string PowerShellClipboard =
        "powershell -Command \"Set-Clipboard(((Get-Content '%~f0' -Raw -Encoding UTF8)-split'goto :eof\\r?\\n',2)[1].TrimEnd())\"";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly ISerializer YamlSerializer =
        new SerializerBuilder()
            .WithTypeConverter(new DateTimeOffsetConverter(
                formats: ["yyyy-MM-ddTHH:mm:ss.fffffffK"]))
            .Build();

    private static readonly IDeserializer YamlDeserializer =
        new DeserializerBuilder().Build();

    public async Task SerializeAsync(TagMark tagMark, Stream stream)
    {
        await using var writer = new StreamWriter(stream, Utf8NoBom, leaveOpen: true);

        if (tagMark.Type == TagMarkType.Uri)
        {
            await writer.WriteLineAsync($"{StartPrefix}{EscapePercent(tagMark.Payload)}\"");
        }
        else
        {
            await writer.WriteLineAsync(EchoOff);
            await writer.WriteLineAsync(PowerShellClipboard);
        }

        await WriteMetadataAsync(writer, tagMark.Metadata);
        await WriteSecondaryUrisAsync(writer, tagMark.SecondaryUris);

        if (tagMark.Type == TagMarkType.Clip)
        {
            await writer.WriteLineAsync(GotoEof);
            foreach (var line in tagMark.Payload.Split('\n'))
                await writer.WriteLineAsync(line.TrimEnd('\r'));
        }
    }

    public async Task<TagMark> DeserializeAsync(Stream stream, string name)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
            lines.Add(line);

        if (lines.Count == 0)
            throw new InvalidDataException($"Empty .bat file for tagmark '{name}'");

        var firstLine = lines[0].TrimStart();
        var type = firstLine.StartsWith(StartPrefix, StringComparison.OrdinalIgnoreCase)
            ? TagMarkType.Uri
            : TagMarkType.Clip;

        string payload;
        IReadOnlyList<string> secondaryUris;
        TagMarkMetadata metadata;

        if (type == TagMarkType.Uri)
        {
            payload = UnescapePercent(ExtractUriFromStartLine(firstLine));
            (metadata, secondaryUris) = ParseRemLines(lines, startIndex: 1);
        }
        else
        {
            payload = ExtractClipPayload(lines);
            int dataEndIndex = FindClipDataEndIndex(lines);
            // Skip line 0 (@echo off) and line 1 (powershell command)
            (metadata, secondaryUris) = ParseRemLines(lines, startIndex: 2, endIndex: dataEndIndex);
        }

        return new TagMark(name, type, payload, metadata, secondaryUris);
    }

    // ── Escaping ─────────────────────────────────────────────────────

    private static string EscapePercent(string value) =>
        value.Replace("%", "%%");

    private static string UnescapePercent(string value) =>
        value.Replace("%%", "%");

    // ── URI extraction ───────────────────────────────────────────────

    private static string ExtractUriFromStartLine(string line)
    {
        int firstQuote = line.IndexOf('"');
        int secondQuote = line.IndexOf('"', firstQuote + 1);
        int thirdQuote = line.IndexOf('"', secondQuote + 1);
        int fourthQuote = line.IndexOf('"', thirdQuote + 1);

        if (thirdQuote >= 0 && fourthQuote > thirdQuote)
            return line.Substring(thirdQuote + 1, fourthQuote - thirdQuote - 1);

        throw new InvalidDataException("Could not extract URI from start line");
    }

    // ── Clip payload extraction ──────────────────────────────────────

    private static string ExtractClipPayload(List<string> lines)
    {
        // Find first goto :eof separator (search forward to match PowerShell -split ,2 behavior)
        int gotoIndex = -1;
        for (int i = 2; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(GotoEof, StringComparison.OrdinalIgnoreCase))
            {
                gotoIndex = i;
                break;
            }
        }

        if (gotoIndex >= 0)
        {
            if (gotoIndex >= lines.Count - 1)
                return string.Empty;

            var payloadLines = new List<string>();
            for (int i = gotoIndex + 1; i < lines.Count; i++)
                payloadLines.Add(lines[i].TrimEnd('\r'));

            // Trim trailing empty lines
            while (payloadLines.Count > 0 && string.IsNullOrEmpty(payloadLines[^1].Trim()))
                payloadLines.RemoveAt(payloadLines.Count - 1);

            if (payloadLines.Count == 0)
                return string.Empty;

            return string.Join("\n", payloadLines).TrimEnd();
        }

        // No goto :eof — legacy format, last non-empty line
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith(RemPrefix, StringComparison.Ordinal))
                return line[RemPrefix.Length..].TrimEnd();
            return line.TrimEnd();
        }
        return string.Empty;
    }

    private static int FindClipDataEndIndex(List<string> lines)
    {
        // Find first goto :eof separator (forward search matches PowerShell -split ,2 behavior)
        for (int i = 2; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Equals(GotoEof, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        // No goto :eof found (backward compat) — data ends before last line
        return lines.Count - 1;
    }

    // ── rem line parsing ─────────────────────────────────────────────

    private (TagMarkMetadata metadata, IReadOnlyList<string> secondaryUris) ParseRemLines(
        List<string> lines, int startIndex, int? endIndex = null)
    {
        int end = endIndex ?? lines.Count;
        var yamlLines = new List<string>();
        var secondaryUris = new List<string>();

        for (int i = startIndex; i < end; i++)
        {
            var line = lines[i];
            if (line.StartsWith(RemLinkPrefix, StringComparison.Ordinal))
            {
                var uri = UnescapePercent(line[RemLinkPrefix.Length..]);
                secondaryUris.Add(uri);
            }
            else if (line.StartsWith(RemPrefix, StringComparison.OrdinalIgnoreCase))
            {
                yamlLines.Add(line[RemPrefix.Length..]);
            }
        }
        
        var metadata = DeserializeMetadata(yamlLines);
        return (metadata, secondaryUris);
    }

    private static TagMarkMetadata DeserializeMetadata(List<string> yamlLines)
    {
        if (yamlLines.Count == 0)
            return new TagMarkMetadata(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0);

        var yamlContent = string.Join("\n", yamlLines);
        var data = YamlDeserializer.Deserialize<MetadataDto>(yamlContent);

        return new TagMarkMetadata(
            data.LastOpened != default ? data.LastOpened : DateTimeOffset.UnixEpoch,
            data.Created != default ? data.Created : DateTimeOffset.UnixEpoch,
            data.TimesOpened);
    }

    // ── Writing helpers ──────────────────────────────────────────────

    private static async Task WriteMetadataAsync(StreamWriter writer, TagMarkMetadata metadata)
    {
        var dto = new MetadataDto
        {
            LastOpened = metadata.LastOpened,
            Created = metadata.Created,
            TimesOpened = metadata.TimesOpened
        };
        var yaml = YamlSerializer.Serialize(dto);
        foreach (var line in yaml.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!string.IsNullOrEmpty(trimmed))
                await writer.WriteLineAsync($"{RemPrefix}{trimmed}");
        }
    }

    private static async Task WriteSecondaryUrisAsync(StreamWriter writer, IReadOnlyList<string> uris)
    {
        foreach (var uri in uris)
            await writer.WriteLineAsync($"{RemLinkPrefix}{EscapePercent(uri)}");
    }

    /// <summary>
    /// Mutable DTO for YamlDotNet serialization/deserialization.
    /// Kept private — external code uses the immutable TagMarkMetadata record.
    /// </summary>
    private class MetadataDto
    {
        public DateTimeOffset LastOpened { get; set; }
        public DateTimeOffset Created { get; set; }
        public uint TimesOpened { get; set; }
    }
}
