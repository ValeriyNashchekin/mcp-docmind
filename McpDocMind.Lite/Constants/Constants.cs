namespace McpDocMind.Lite.Constants;

/// <summary>
/// Embedding vector configuration.
/// </summary>
public static class EmbeddingConstants
{
    /// <summary>384 dimensions for bge-micro-v2 model.</summary>
    public const int VectorDimension = 384;
}

/// <summary>
/// Priority scores for node types in search ranking.
/// </summary>
public static class NodeTypePriority
{
    private static readonly Dictionary<string, int> Priorities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Class"] = 100,
        ["Interface"] = 95,
        ["Struct"] = 90,
        ["Enum"] = 85,
        ["Delegate"] = 80,
        ["TypeAlias"] = 75,
        ["Namespace"] = 72,
        ["Constructor"] = 70,
        ["Method"] = 60,
        ["Property"] = 50,
        ["Event"] = 40,
        ["Field"] = 30
    };

    public static int GetPriority(string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType)) return 0;
        return Priorities.TryGetValue(nodeType, out var p) ? p : 0;
    }
}

/// <summary>
/// Search and query configuration constants.
/// </summary>
public static class SearchConstants
{
    public const int DefaultSearchLimit = 10;
    public const int DefaultTypeLimit = 100;
    public const int ApiComparisonLimit = 1000;
    public const int MaxResultTokens = 4000;
    public const double RelevanceDropThreshold = 0.5;
    public const double ExactMatchBoost = 1.0;
    public const double TitleWeight = 10.0;
    public const double ContentWeight = 1.0;
    public const double Bm25Weight = 0.4;
    public const double VectorWeight = 0.6;
}

/// <summary>
/// Constants for document ingestion and chunking.
/// </summary>
public static class IngestionConstants
{
    public const int MaxChunkTokens = 800;
    public const int HardLimitTokens = 1200;
    public const int MinChunkTokens = 50;

    public static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "code_of_conduct.md", "contributing.md", "changelog.md", "history.md",
        "license.md", "security.md", "pull_request_template.md", "issue_template.md",
        "bug_report.md", "feature_request.md", "authors.md", "contributors.md",
        "support.md", "funding.md", "codeowners", "maintainers.md"
    };

    public static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "__tests__", "__test__", "test", "tests", "spec", "specs",
        "fixtures", "__fixtures__", "__mocks__",
        ".github", ".vscode", ".idea",
        "node_modules", "vendor", "dist", "build", "out",
        ".next", ".nuxt", ".cache",
        "coverage", "benchmark", "benchmarks",
        "examples", "example", "e2e",
        "cypress", "playwright",
        "obj", "bin", ".git", "packages", "target"
    };

    public static readonly HashSet<string> LocaleCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "bg", "bn", "ca", "cs", "da", "de", "el", "es", "et", "fa", "fi",
        "fr", "he", "hi", "hr", "hu", "id", "it", "ja", "ko", "lt", "lv", "ms",
        "nl", "no", "pl", "pt", "ro", "ru", "sk", "sl", "sr", "sv", "th", "tr",
        "uk", "vi", "zh", "zh-cn", "zh-tw", "zh-hans", "zh-hant", "pt-br", "es-la"
    };

    public static readonly string[] MarkdownExtensions = [".md", ".mdx"];
}
