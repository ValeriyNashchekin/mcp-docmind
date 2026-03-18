using Microsoft.Data.Sqlite;
using McpDocMind.Lite.Constants;
using McpDocMind.Lite.Database;
using McpDocMind.Lite.Embeddings;
using McpDocMind.Lite.Models;

namespace McpDocMind.Lite.Search;

/// <summary>
/// Hybrid search: FTS5 (keyword) + vector (semantic) with score merging.
/// </summary>
public sealed class HybridSearchService(AppDatabase db, EmbeddingService embeddings)
{
    /// <summary>
    /// Full hybrid search: FTS5 + vector, merged and ranked.
    /// </summary>
    public List<SearchResult> SearchHybrid(string query, string? library = null,
        string? version = null, int limit = SearchConstants.DefaultSearchLimit)
    {
        var ftsResults = SearchFts(query, library, version, limit * 2);
        var vecResults = SearchSemantic(query, library, version, limit * 2);

        // RRF (Reciprocal Rank Fusion) merge
        const double k = 60.0;
        var scores = new Dictionary<string, (SearchResult Result, double Score)>();

        for (int i = 0; i < ftsResults.Count; i++)
        {
            var key = $"{ftsResults[i].Type}:{ftsResults[i].Id}";
            var rrfScore = 1.0 / (k + i + 1) * SearchConstants.Bm25Weight;
            scores[key] = (ftsResults[i], rrfScore);
        }

        for (int i = 0; i < vecResults.Count; i++)
        {
            var key = $"{vecResults[i].Type}:{vecResults[i].Id}";
            var rrfScore = 1.0 / (k + i + 1) * SearchConstants.VectorWeight;
            if (scores.TryGetValue(key, out var existing))
            {
                existing.Result.Source = "hybrid";
                scores[key] = (existing.Result, existing.Score + rrfScore);
            }
            else
            {
                vecResults[i].Source = "semantic";
                scores[key] = (vecResults[i], rrfScore);
            }
        }

        foreach (var (_, (result, score)) in scores)
            result.Score = score;

        var results = scores.Values
            .OrderByDescending(x => x.Score)
            .Select(x => x.Result)
            .Take(limit)
            .ToList();

        return FilterByRelevance(ApplyTokenBudget(results));
    }

    /// <summary>
    /// Full-text search using FTS5 BM25 ranking.
    /// </summary>
    public List<SearchResult> SearchFts(string query, string? library = null,
        string? version = null, int limit = SearchConstants.DefaultSearchLimit)
    {
        var ftsQuery = QueryAnalyzer.BuildFts5Query(query);
        var results = new List<SearchResult>();

        using var conn = db.CreateConnection();

        // Search API nodes
        var apiSql = """
            SELECT n.Id, n.FullName, n.Name, n.NodeType, n.Summary, n.Declaration,
                   n.LibraryName, n.ApiVersion,
                   bm25(ApiNodes_fts, 10.0, 5.0, 1.0, 1.0) as rank
            FROM ApiNodes_fts f
            JOIN ApiNodes n ON n.Id = f.rowid
            WHERE ApiNodes_fts MATCH @query
            """;

        if (library is not null) apiSql += " AND n.LibraryName = @library";
        if (version is not null) apiSql += " AND n.ApiVersion = @version";
        apiSql += " ORDER BY rank LIMIT @limit";

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = apiSql;
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", limit);
            if (library is not null) cmd.Parameters.AddWithValue("@library", library);
            if (version is not null) cmd.Parameters.AddWithValue("@version", version);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SearchResult
                {
                    Id = reader.GetInt64(0),
                    FullName = reader.GetString(1),
                    Title = reader.GetString(2),
                    NodeType = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Content = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Declaration = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Library = reader.GetString(6),
                    Version = reader.GetString(7),
                    Score = Math.Abs(reader.GetDouble(8)), // BM25 returns negative
                    Type = "api",
                    Source = "fts"
                });
            }
        }

        // Search doc chunks
        var docSql = """
            SELECT c.Id, c.Source, c.Section, c.Content, c.LibraryName, c.ApiVersion,
                   c.Tokens, c.ChunkIndex,
                   bm25(DocChunks_fts, 5.0, 3.0, 1.0) as rank
            FROM DocChunks_fts f
            JOIN DocChunks c ON c.Id = f.rowid
            WHERE DocChunks_fts MATCH @query
            """;

        if (library is not null) docSql += " AND c.LibraryName = @library";
        if (version is not null) docSql += " AND c.ApiVersion = @version";
        docSql += " ORDER BY rank LIMIT @limit";

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = docSql;
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", limit);
            if (library is not null) cmd.Parameters.AddWithValue("@library", library);
            if (version is not null) cmd.Parameters.AddWithValue("@version", version);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SearchResult
                {
                    Id = reader.GetInt64(0),
                    Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Content = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Library = reader.GetString(4),
                    Version = reader.GetString(5),
                    Tokens = reader.GetInt32(6),
                    ChunkOrder = reader.GetInt32(7),
                    Score = Math.Abs(reader.GetDouble(8)),
                    Type = "docs",
                    Source = "fts"
                });
            }
        }

        return results.OrderByDescending(r => r.Score).Take(limit).ToList();
    }

    /// <summary>
    /// Semantic search using embedding cosine distance.
    /// Embeddings stored as BLOBs; brute-force cosine distance in SQL.
    /// </summary>
    public List<SearchResult> SearchSemantic(string query, string? library = null,
        string? version = null, int limit = SearchConstants.DefaultSearchLimit)
    {
        var queryEmbedding = embeddings.GenerateEmbedding(query);
        var queryBlob = EmbeddingService.ToBlob(queryEmbedding);
        var results = new List<SearchResult>();

        using var conn = db.CreateConnection();

        // API nodes with embeddings
        var apiSql = """
            SELECT n.Id, n.FullName, n.Name, n.NodeType, n.Summary, n.Declaration,
                   n.LibraryName, n.ApiVersion, e.Embedding
            FROM ApiEmbeddings e
            JOIN ApiNodes n ON n.Id = e.NodeId
            WHERE 1=1
            """;
        if (library is not null) apiSql += " AND n.LibraryName = @library";
        if (version is not null) apiSql += " AND n.ApiVersion = @version";

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = apiSql;
            if (library is not null) cmd.Parameters.AddWithValue("@library", library);
            if (version is not null) cmd.Parameters.AddWithValue("@version", version);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var embBlob = (byte[])reader.GetValue(8);
                var emb = EmbeddingService.FromBlob(embBlob);
                var distance = CosineDistance(queryEmbedding, emb);

                results.Add(new SearchResult
                {
                    Id = reader.GetInt64(0),
                    FullName = reader.GetString(1),
                    Title = reader.GetString(2),
                    NodeType = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Content = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Declaration = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Library = reader.GetString(6),
                    Version = reader.GetString(7),
                    Score = 1.0 - distance, // Convert distance to similarity
                    Type = "api",
                    Source = "semantic"
                });
            }
        }

        // Doc chunks with embeddings
        var docSql = """
            SELECT c.Id, c.Source, c.Section, c.Content, c.LibraryName, c.ApiVersion,
                   c.Tokens, c.ChunkIndex, e.Embedding
            FROM DocEmbeddings e
            JOIN DocChunks c ON c.Id = e.ChunkId
            WHERE 1=1
            """;
        if (library is not null) docSql += " AND c.LibraryName = @library";
        if (version is not null) docSql += " AND c.ApiVersion = @version";

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = docSql;
            if (library is not null) cmd.Parameters.AddWithValue("@library", library);
            if (version is not null) cmd.Parameters.AddWithValue("@version", version);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var embBlob = (byte[])reader.GetValue(8);
                var emb = EmbeddingService.FromBlob(embBlob);
                var distance = CosineDistance(queryEmbedding, emb);

                results.Add(new SearchResult
                {
                    Id = reader.GetInt64(0),
                    Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Content = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Library = reader.GetString(4),
                    Version = reader.GetString(5),
                    Tokens = reader.GetInt32(6),
                    ChunkOrder = reader.GetInt32(7),
                    Score = 1.0 - distance,
                    Type = "docs",
                    Source = "semantic"
                });
            }
        }

        return results.OrderByDescending(r => r.Score).Take(limit).ToList();
    }

    /// <summary>
    /// Filters results keeping only those with score >= threshold fraction of the top score.
    /// </summary>
    private static List<SearchResult> FilterByRelevance(List<SearchResult> results)
    {
        if (results.Count <= 5) return results;
        var topScore = results[0].Score;
        if (topScore <= 0) return results;
        var threshold = topScore * SearchConstants.RelevanceDropThreshold;
        var filtered = results.Where(r => r.Score >= threshold).ToList();
        return filtered.Count < 5 ? results.Take(5).ToList() : filtered;
    }

    /// <summary>
    /// Trims results to fit within the token budget.
    /// </summary>
    private static List<SearchResult> ApplyTokenBudget(List<SearchResult> results)
    {
        var budget = SearchConstants.MaxResultTokens;
        var output = new List<SearchResult>();
        foreach (var r in results)
        {
            var tokens = r.Tokens > 0 ? r.Tokens : EstimateTokens(r.Content);
            if (budget - tokens < 0 && output.Count > 0) break;
            budget -= tokens;
            output.Add(r);
        }
        return output;
    }

    private static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 10 : text.Length / 4;

    /// <summary>
    /// Cosine distance between two vectors (0 = identical, 2 = opposite).
    /// </summary>
    private static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom < 1e-10 ? 1.0 : 1.0 - dot / denom;
    }
}
