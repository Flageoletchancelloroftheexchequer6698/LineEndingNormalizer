using System.Text.RegularExpressions;

namespace LineEndingNormalizer;

/// <summary>
/// Provides wildcard filename matching using regular expressions.
/// </summary>
internal static partial class FilePatternMatcher
{
    [GeneratedRegex(
        "^.*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MatchAllRegex();

    /// <summary>
    /// Determines whether the specified filename matches any wildcard pattern.
    /// Supports '*' and '?' using a case-insensitive comparison.
    /// </summary>
    public static bool IsMatch(
        string fileName,
        IEnumerable<Regex> patterns)
    {
        foreach (Regex pattern in patterns)
        {
            if (pattern.IsMatch(fileName))
            {
                return true;
            }
        }

        return false;
    }


    /// <summary>
    /// Compiles wildcard patterns into regular expressions.
    /// </summary>
    public static List<Regex> Compile(
        List<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var result =
            new List<Regex>(patterns.Count);

        foreach (string pattern in patterns)
        {
            string fileMask =
                pattern.Trim();

            if (fileMask.Length == 0)
            {
                continue;
            }

            result.Add(
                new Regex(
                    "^" +
                    Regex.Escape(fileMask)
                        .Replace(@"\*", ".*")
                        .Replace(@"\?", ".") +
                    "$",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant |
                    RegexOptions.Compiled));
        }

        // Treat an empty pattern list as "*".
        if (result.Count == 0)
        {
            result.Add(MatchAllRegex());
        }

        return result;
    }
}
