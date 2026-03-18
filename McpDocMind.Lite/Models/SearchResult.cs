namespace McpDocMind.Lite.Models;

/// <summary>
/// Unified search result from hybrid search (FTS + vector).
/// </summary>
public sealed class SearchResult
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? FullName { get; set; }
    public string? NodeType { get; set; }
    public string? Declaration { get; set; }
    public string Library { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>"api" or "docs"</summary>
    public string Type { get; set; } = "api";

    public double Score { get; set; }

    /// <summary>"fts", "semantic", or "hybrid"</summary>
    public string? Source { get; set; }

    public int Tokens { get; set; }
    public int ChunkOrder { get; set; }
}

/// <summary>
/// Library metadata.
/// </summary>
public sealed class LibraryInfo
{
    public string LibraryName { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public string Type { get; set; } = "api";
}
