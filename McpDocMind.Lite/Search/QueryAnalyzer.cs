using System.Text.RegularExpressions;

namespace McpDocMind.Lite.Search;

/// <summary>
/// Query analysis: keyword extraction, stop-word filtering, FTS5 query building.
/// </summary>
public static partial class QueryAnalyzer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "can", "shall", "to", "of", "in", "for",
        "on", "with", "at", "by", "from", "as", "into", "about", "between",
        "through", "during", "before", "after", "and", "but", "or", "not",
        "this", "that", "these", "those", "it", "its", "what", "which",
        "who", "whom", "how", "when", "where", "why", "all", "each",
        "every", "any", "few", "more", "most", "other", "some", "such",
        "than", "too", "very", "just", "also", "use", "uses", "used",
        "using", "get", "gets", "got", "make", "makes", "made", "find",
        "finds", "show", "shows", "create", "creates", "created"
    };

    [GeneratedRegex(@"(?<!^)(?=[A-Z][a-z])", RegexOptions.Compiled)]
    private static partial Regex CamelCaseRegex();

    /// <summary>
    /// Extracts meaningful keywords from a query, splitting CamelCase and filtering stop words.
    /// </summary>
    public static List<string> ExtractKeywords(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var keywords = new List<string>();
        var parts = query.Split([' ', '_', '.', '-'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var camelParts = CamelCaseRegex().Split(part);
            foreach (var cp in camelParts)
            {
                if (!string.IsNullOrWhiteSpace(cp) && cp.Length > 2 && !StopWords.Contains(cp))
                    keywords.Add(cp.ToLowerInvariant());
            }
            if (part.Length > 2 && !StopWords.Contains(part))
                keywords.Add(part.ToLowerInvariant());
        }

        return keywords.Distinct().ToList();
    }

    /// <summary>
    /// Builds an FTS5 MATCH query from user input.
    /// </summary>
    public static string BuildFts5Query(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "\"\"";

        var trimmed = query.Trim();
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            return trimmed; // phrase search

        // Escape double quotes
        var escaped = trimmed.Replace("\"", "\"\"");

        // Multi-word: join with AND
        if (escaped.Contains(' '))
        {
            var parts = escaped.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1
                ? string.Join(" AND ", parts.Select(p => $"\"{p}\""))
                : $"\"{parts[0]}\"";
        }

        return $"\"{escaped}\"";
    }

    /// <summary>
    /// Calculates the fraction of keywords found in a text (0.0 to 1.0).
    /// </summary>
    public static double CalculateExactMatchScore(string text, List<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text) || keywords.Count == 0) return 0.0;
        var lower = text.ToLowerInvariant();
        return (double)keywords.Count(k => lower.Contains(k)) / keywords.Count;
    }
}
