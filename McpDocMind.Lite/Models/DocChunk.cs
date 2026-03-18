namespace McpDocMind.Lite.Models;

/// <summary>
/// Represents a chunk of documentation content with semantic embedding.
/// </summary>
public sealed class DocChunk
{
    public long Id { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Section { get; set; }
    public string Content { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public bool HasCode { get; set; }
    public string? ContentHash { get; set; }
    public int Tokens { get; set; }

    // Query-only
    public double Score { get; set; }
}
