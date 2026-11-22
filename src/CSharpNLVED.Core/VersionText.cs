using System.Text.RegularExpressions;

namespace CSharpNLVED.Core;

public static partial class VersionText
{
    /// <summary>
    /// Pulls the first dotted number out of a version string:
    /// "9.0.2-full_build-www.gyan.dev" → 9.0.2, "n9.0-12-gabc" → 9.0, "9.0.2" → 9.0.2.
    /// </summary>
    public static Version? TryParseNumeric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = NumberPattern().Match(text);
        if (!match.Success)
            return null;

        var value = match.Value.Contains('.') ? match.Value : match.Value + ".0";
        return Version.TryParse(value, out var version) ? version : null;
    }

    [GeneratedRegex(@"\d+(?:\.\d+){0,3}")]
    private static partial Regex NumberPattern();
}
