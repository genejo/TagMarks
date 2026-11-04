using System.Text;

namespace Cauldron.Services;

public readonly record struct SanitizeResult(string Text, bool CharsRemoved, bool Truncated);

public static class Validation {
    // Uri.TryCreate escapes whitespace silently, which would make text like "Note: buy milk" a URI with
    // scheme "note". A space counts only after "scheme://host", where a sloppily copied link has one
    // (spec §4.1, §11 #20); other whitespace never does. Callers join wrapped lines beforehand.
    public static bool IsValidUri(string text) {
        string trimmed = text.Trim();
        return HasUriWhitespaceOnlyInPath(trimmed)
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme.Length > 1;
    }

    private static bool HasUriWhitespaceOnlyInPath(string text) {
        if (text.Any(c => char.IsWhiteSpace(c) && c != ' ')) return false;
        if (!text.Contains(' ')) return true;
        int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return false;
        int authorityStart = schemeEnd + 3;
        int authorityEnd = text.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0) return false;
        return text.IndexOf(' ', 0, authorityEnd) < 0;
    }

    public const int MaxClipPayloadLength = 16_000;
    public static bool IsClipPayloadTooLong(string text) => text.Length > MaxClipPayloadLength;

    // Union of invalid filename characters across Windows, macOS, and Linux.
    // Windows is the strictest: < > : " / \ | ? * plus control chars 0–31.
    // macOS adds : and /; Linux adds / and \0. This array covers all three.
    private static readonly char[] InvalidFileNameChars =
        ['"', '<', '>', '|', '\0', '\x01', '\x02', '\x03', '\x04', '\x05',
         '\x06', '\x07', '\x08', '\x09', '\x0A', '\x0B', '\x0C', '\x0D',
         '\x0E', '\x0F', '\x10', '\x11', '\x12', '\x13', '\x14', '\x15',
         '\x16', '\x17', '\x18', '\x19', '\x1A', '\x1B', '\x1C', '\x1D',
         '\x1E', '\x1F', ':', '*', '?', '\\', '/'];

    private static readonly HashSet<char> InvalidFileNameCharSet = new(InvalidFileNameChars);

    public static bool HasValidFileNameChars(string name) =>
        name.IndexOfAny(InvalidFileNameChars) < 0;

    // Starts a filter token such as ":dup" (spec §3.2). It can't be part of a name, so a token never
    // matches one by accident.
    public const char FilterTokenPrefix = ':';

    /// <summary>Filter text may hold anything a name may, plus <see cref="FilterTokenPrefix"/>.</summary>
    public static bool HasValidFilterChars(string text) =>
        HasValidFileNameChars(text.Replace(FilterTokenPrefix.ToString(), string.Empty));

    /// <summary>Like <see cref="SanitizeFileName"/>, but keeps <see cref="FilterTokenPrefix"/>.</summary>
    public static SanitizeResult SanitizeFilterText(string text, int maxLength = 255) =>
        Sanitize(text, maxLength, keep: FilterTokenPrefix);

    public static SanitizeResult SanitizeFileName(string name, int maxLength = 255) =>
        Sanitize(name, maxLength, keep: null);

    private static SanitizeResult Sanitize(string name, int maxLength, char? keep)
    {
        if (string.IsNullOrEmpty(name))
            return new SanitizeResult(name ?? "", false, false);

        var sb = new StringBuilder(name.Length);
        bool charsRemoved = false;

        foreach (char c in name)
        {
            if (c != keep && InvalidFileNameCharSet.Contains(c))
                charsRemoved = true;
            else
                sb.Append(c);
        }

        bool truncated = sb.Length > maxLength;
        if (truncated)
            sb.Length = maxLength;

        return new SanitizeResult(sb.ToString(), charsRemoved, truncated);
    }
}
