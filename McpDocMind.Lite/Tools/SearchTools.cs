using System.ComponentModel;
using System.Text.Json;
using McpDocMind.Lite.Search;
using McpDocMind.Lite.Models;
using ModelContextProtocol.Server;

namespace McpDocMind.Lite.Tools;

[McpServerToolType]
public sealed class SearchTools(HybridSearchService search)
{
    [McpServerTool(Name = "search"), Description("Search across all libraries (API and documentation). Uses hybrid search: FTS for exact matches + semantic for relevance.")]
    public string Search(
        [Description("Search query")] string query,
        [Description("Filter by library name (optional)")] string? library = null,
        [Description("Filter by version (optional)")] string? version = null,
        [Description("Max results (default: 20)")] int limit = 20)
    {
        var results = search.SearchHybrid(query, library, version, limit);
        return FormatResults(results);
    }

    private static string FormatResults(List<SearchResult> results)
    {
        if (results.Count == 0) return "No results found.";

        return JsonSerializer.Serialize(results.Select(r => new
        {
            r.Type,
            r.Title,
            r.FullName,
            r.NodeType,
            Content = CleanSummary(r.Content),
            r.Declaration,
            r.Library,
            r.Version,
            Score = Math.Round(r.Score, 4)
        }), new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? CleanSummary(string? summary)
    {
        if (string.IsNullOrEmpty(summary)) return summary;

        var remarksIdx = summary.IndexOf("\nRemarks:", StringComparison.OrdinalIgnoreCase);
        if (remarksIdx < 0) remarksIdx = summary.IndexOf("\r\nRemarks:", StringComparison.OrdinalIgnoreCase);
        if (remarksIdx > 0) summary = summary[..remarksIdx];

        summary = summary.Replace("\r\n", " ").Replace("\n", " ").Trim();
        if (summary.Length > 200) summary = summary[..197] + "...";

        return summary;
    }
}
