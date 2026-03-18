using Microsoft.Data.Sqlite;
using McpDocMind.Lite.Database;
using McpDocMind.Lite.Embeddings;
using McpDocMind.Lite.Models;

namespace McpDocMind.Lite.Ingestion;

/// <summary>
/// Orchestrates ingestion: parsing → embedding → SQLite storage.
/// </summary>
public sealed class IngestService(AppDatabase db, EmbeddingService embeddings)
{
    private readonly DllParser _dllParser = new();
    private readonly MarkdownParser _mdParser = new();
    private readonly GitParser _gitParser = new();
    private readonly TypeScriptParser _tsParser = new();

    /// <summary>
    /// Ingest a .NET DLL (and optional XML doc file).
    /// </summary>
    public async Task<int> IngestDllAsync(string dllPath, string libraryName, string apiVersion,
        string? xmlPath = null, CancellationToken ct = default)
    {
        // Use explicit XML path, or auto-detect next to DLL
        xmlPath ??= Path.ChangeExtension(dllPath, ".xml");
        var xmlDocs = File.Exists(xmlPath) ? XmlDocParser.Parse(xmlPath) : null;

        var (nodes, _) = _dllParser.ParseDll(dllPath, libraryName, apiVersion, xmlDocs);
        if (nodes.Count == 0) return 0;

        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        var insertedCount = 0;
        var nodeIdMap = new Dictionary<string, long>(); // FullName → DB Id

        // Insert nodes
        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();

            var id = InsertApiNode(conn, tx, node);
            if (id > 0)
            {
                nodeIdMap[node.FullName] = id;
                insertedCount++;

                // Generate and store embedding
                var text = $"{node.FullName} {node.Summary ?? ""}".Trim();
                var embedding = embeddings.GenerateEmbedding(text);
                InsertEmbedding(conn, tx, "ApiEmbeddings", "NodeId", id, embedding);
            }
        }

        // Insert relations (Contains, InheritsFrom)
        foreach (var node in nodes)
        {
            if (node.ParentType is null) continue;
            if (!nodeIdMap.TryGetValue(node.FullName, out var childId)) continue;

            // Contains relation
            if (nodeIdMap.TryGetValue(node.ParentType, out var parentId))
            {
                InsertRelation(conn, tx, parentId, childId, "Contains");
            }

            // InheritsFrom (from ApiNode.ParentType if it's a type)
            if (node.NodeType is "Class" or "Interface" or "Struct" && node.ParentType is not null)
            {
                // Try to find parent type in our nodes
                var baseNodes = nodes.Where(n => n.FullName == node.ParentType && n.NodeType is "Class" or "Interface").ToList();
                foreach (var baseNode in baseNodes)
                {
                    if (nodeIdMap.TryGetValue(baseNode.FullName, out var baseId))
                    {
                        var relType = baseNode.NodeType == "Interface" ? "Implements" : "InheritsFrom";
                        InsertRelation(conn, tx, baseId, childId, relType);
                    }
                }
            }
        }

        tx.Commit();
        return insertedCount;
    }

    /// <summary>
    /// Ingest markdown files from a directory.
    /// </summary>
    public async Task<int> IngestMarkdownAsync(string path, string libraryName, string version,
        CancellationToken ct = default)
    {
        List<DocChunk> chunks;

        if (Directory.Exists(path))
            chunks = await _mdParser.ParseDirectoryAsync(path, libraryName, version, ct);
        else if (File.Exists(path))
            chunks = await _mdParser.ParseFileAsync(path, libraryName, version, ct);
        else
            throw new FileNotFoundException($"Path not found: {path}");

        return await InsertDocChunksAsync(chunks, libraryName, version, ct);
    }

    /// <summary>
    /// Ingest a git repository: markdown docs + optional TypeScript/JS API extraction.
    /// Single clone — both markdown and TS parsing use the same temp directory.
    /// </summary>
    public async Task<RepositoryIngestResult> IngestRepositoryAsync(string repoUrl, string libraryName,
        string apiVersion, string? docsPath = null, string? branch = null,
        bool parseTypeScript = true, CancellationToken ct = default)
    {
        Console.Error.WriteLine($"[1/5] Cloning repository: {repoUrl}...");
        
        // Single clone — GitParser returns the temp dir for reuse
        var (chunks, tempDir) = await _gitParser.CloneAndParseAsync(
            repoUrl, libraryName, apiVersion, docsPath, branch, ct);

        try
        {
            Console.Error.WriteLine($"[2/5] Preparing database (purging old data for {libraryName} v{apiVersion})...");
            
            // Strategy A: Purge old data only after successful clone/initial parse
            PurgeLibrary(libraryName, apiVersion);

            Console.Error.WriteLine($"[3/5] Ingesting {chunks.Count} documentation chunks...");
            var docCount = await InsertDocChunksAsync(chunks, libraryName, apiVersion, ct);
            
            var nodeCount = 0;
            var relationCount = 0;

            // Auto-detect and parse TypeScript/JS from the same cloned directory
            if (parseTypeScript && TypeScriptParser.DetectTypeScript(tempDir))
            {
                Console.Error.WriteLine("[4/5] TypeScript/JS detected — extracting API...");
                try
                {
                    var tsResult = await _tsParser.ParseAsync(tempDir, libraryName, apiVersion, ct);
                    if (tsResult.Nodes.Count > 0)
                    {
                        Console.Error.WriteLine($"[5/5] Ingesting {tsResult.Nodes.Count} API nodes...");
                        (nodeCount, relationCount) = await IngestTypeScriptAsync(
                            tsResult.Nodes, tsResult.Relations, ct);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: TypeScript extraction failed: {ex.Message}");
                }
            }
            else
            {
                Console.Error.WriteLine("[4/5] No TypeScript/JS API detected or parsing disabled.");
            }

            return new RepositoryIngestResult(docCount, nodeCount, relationCount);
        }
        finally
        {
            await GitParser.CleanupTempDir(tempDir, ct);
        }
    }

    /// <summary>
    /// Ingest TypeScript API nodes and relations into the database.
    /// </summary>
    public async Task<(int NodeCount, int RelationCount)> IngestTypeScriptAsync(
        List<ApiNode> nodes, List<TsResolvedRelation> relations,
        CancellationToken ct = default)
    {
        if (nodes.Count == 0) return (0, 0);

        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        var nodeIdMap = new Dictionary<string, long>(StringComparer.Ordinal);
        var insertedNodes = 0;

        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();

            var id = InsertApiNode(conn, tx, node);
            if (id > 0)
            {
                nodeIdMap[node.FullName] = id;
                insertedNodes++;

                var text = $"{node.FullName} {node.Summary ?? ""}".Trim();
                var embedding = embeddings.GenerateEmbedding(text);
                InsertEmbedding(conn, tx, "ApiEmbeddings", "NodeId", id, embedding);
            }
        }

        // Insert resolved relations
        var insertedRelations = 0;
        foreach (var rel in relations)
        {
            ct.ThrowIfCancellationRequested();

            if (!nodeIdMap.TryGetValue(rel.SourceFullName, out var sourceId)) continue;
            if (!nodeIdMap.TryGetValue(rel.TargetFullName, out var targetId)) continue;

            // For Contains: source is parent, target is child
            // For InheritsFrom/Implements: source is child, target is parent
            if (rel.RelationType == "Contains")
                InsertRelation(conn, tx, sourceId, targetId, rel.RelationType);
            else
                InsertRelation(conn, tx, targetId, sourceId, rel.RelationType);

            insertedRelations++;
        }

        tx.Commit();
        return (insertedNodes, insertedRelations);
    }

    private async Task<int> InsertDocChunksAsync(List<DocChunk> chunks, string libraryName,
        string version, CancellationToken ct)
    {
        if (chunks.Count == 0) return 0;

        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        var insertedCount = 0;

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var id = InsertDocChunk(conn, tx, chunk);
            if (id > 0)
            {
                insertedCount++;
                var embedding = embeddings.GenerateEmbedding(
                    $"{chunk.Section ?? ""} {chunk.Content}".Trim());
                InsertEmbedding(conn, tx, "DocEmbeddings", "ChunkId", id, embedding);
            }
        }

        // Upsert doc library metadata
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO DocLibraries (LibraryName, Version, ChunkCount)
                VALUES (@lib, @ver, @count)
                ON CONFLICT(LibraryName, Version) DO UPDATE SET ChunkCount = ChunkCount + @count
                """;
            cmd.Parameters.AddWithValue("@lib", libraryName);
            cmd.Parameters.AddWithValue("@ver", version);
            cmd.Parameters.AddWithValue("@count", insertedCount);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return insertedCount;
    }

    private static long InsertApiNode(SqliteConnection conn, SqliteTransaction tx, ApiNode node)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ApiNodes (LibraryName, ApiVersion, NodeType, FullName, Name, Namespace,
                                  Summary, Declaration, ReturnType, Parameters, ParentType)
            VALUES (@lib, @ver, @type, @full, @name, @ns, @sum, @decl, @ret, @params, @parent)
            ON CONFLICT(LibraryName, ApiVersion, FullName) DO NOTHING
            RETURNING Id
            """;
        cmd.Parameters.AddWithValue("@lib", node.LibraryName);
        cmd.Parameters.AddWithValue("@ver", node.ApiVersion);
        cmd.Parameters.AddWithValue("@type", node.NodeType);
        cmd.Parameters.AddWithValue("@full", node.FullName);
        cmd.Parameters.AddWithValue("@name", node.Name);
        cmd.Parameters.AddWithValue("@ns", (object?)node.Namespace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sum", (object?)node.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@decl", (object?)node.Declaration ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ret", (object?)node.ReturnType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@params", (object?)node.Parameters ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parent", (object?)node.ParentType ?? DBNull.Value);

        var result = cmd.ExecuteScalar();
        return result is long id ? id : 0;
    }

    private static long InsertDocChunk(SqliteConnection conn, SqliteTransaction tx, DocChunk chunk)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO DocChunks (LibraryName, ApiVersion, Source, Section, Content,
                                   ChunkIndex, HasCode, ContentHash, Tokens)
            VALUES (@lib, @ver, @src, @sec, @content, @idx, @code, @hash, @tokens)
            ON CONFLICT(LibraryName, ApiVersion, ContentHash) DO NOTHING
            RETURNING Id
            """;
        cmd.Parameters.AddWithValue("@lib", chunk.LibraryName);
        cmd.Parameters.AddWithValue("@ver", chunk.ApiVersion);
        cmd.Parameters.AddWithValue("@src", chunk.Source);
        cmd.Parameters.AddWithValue("@sec", (object?)chunk.Section ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@content", chunk.Content);
        cmd.Parameters.AddWithValue("@idx", chunk.ChunkIndex);
        cmd.Parameters.AddWithValue("@code", chunk.HasCode ? 1 : 0);
        cmd.Parameters.AddWithValue("@hash", (object?)chunk.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tokens", chunk.Tokens);

        var result = cmd.ExecuteScalar();
        return result is long id ? id : 0;
    }

    private static void InsertRelation(SqliteConnection conn, SqliteTransaction tx,
        long parentId, long childId, string relationType)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO ApiRelations (ParentId, ChildId, RelationType)
            VALUES (@parent, @child, @type)
            """;
        cmd.Parameters.AddWithValue("@parent", parentId);
        cmd.Parameters.AddWithValue("@child", childId);
        cmd.Parameters.AddWithValue("@type", relationType);
        cmd.ExecuteNonQuery();
    }

    private static void InsertEmbedding(SqliteConnection conn, SqliteTransaction tx,
        string table, string idColumn, long id, float[] embedding)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT OR IGNORE INTO {table} ({idColumn}, Embedding) VALUES (@id, @emb)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@emb", EmbeddingService.ToBlob(embedding));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Merge XML documentation into existing API nodes (update Summary).
    /// </summary>
    public int MergeXmlDocs(string xmlPath, string libraryName, string apiVersion)
    {
        var xmlDocs = XmlDocParser.Parse(xmlPath);
        if (xmlDocs.Count == 0) return 0;

        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        var updated = 0;

        foreach (var (xmlKey, doc) in xmlDocs)
        {
            if (string.IsNullOrEmpty(doc.Summary)) continue;

            // xmlKey format: "T:Namespace.TypeName" or "M:Namespace.TypeName.Method" etc.
            var fullName = xmlKey.Length > 2 ? xmlKey[2..] : xmlKey;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE ApiNodes SET Summary = @summary
                WHERE FullName = @fullName AND LibraryName = @lib AND ApiVersion = @ver
                AND (Summary IS NULL OR Summary = '')
                """;
            cmd.Parameters.AddWithValue("@summary", doc.Summary);
            cmd.Parameters.AddWithValue("@fullName", fullName);
            cmd.Parameters.AddWithValue("@lib", libraryName);
            cmd.Parameters.AddWithValue("@ver", apiVersion);
            updated += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return updated;
    }

    // ─── Version-aware ingestion helpers ───

    /// <summary>Returns the NuGet/package version last ingested for this library, or null.</summary>
    public string? GetIngestedVersion(string libraryName, string apiVersion)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PackageVersion FROM IngestedPackages WHERE LibraryName = @lib AND ApiVersion = @ver";
        cmd.Parameters.AddWithValue("@lib", libraryName);
        cmd.Parameters.AddWithValue("@ver", apiVersion);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Deletes all data (nodes, embeddings, relations, chunks) for a library+version.</summary>
    public void PurgeLibrary(string libraryName, string apiVersion)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Delete in correct order for CASCADE (embeddings → relations → nodes)
        Execute(conn, tx, "DELETE FROM ApiEmbeddings WHERE NodeId IN (SELECT Id FROM ApiNodes WHERE LibraryName = @lib AND ApiVersion = @ver)",
            ("@lib", libraryName), ("@ver", apiVersion));
        Execute(conn, tx, "DELETE FROM ApiRelations WHERE ParentId IN (SELECT Id FROM ApiNodes WHERE LibraryName = @lib AND ApiVersion = @ver) OR ChildId IN (SELECT Id FROM ApiNodes WHERE LibraryName = @lib AND ApiVersion = @ver)",
            ("@lib", libraryName), ("@ver", apiVersion));
        Execute(conn, tx, "DELETE FROM ApiNodes WHERE LibraryName = @lib AND ApiVersion = @ver",
            ("@lib", libraryName), ("@ver", apiVersion));
        Execute(conn, tx, "DELETE FROM DocEmbeddings WHERE ChunkId IN (SELECT Id FROM DocChunks WHERE LibraryName = @lib AND ApiVersion = @ver)",
            ("@lib", libraryName), ("@ver", apiVersion));
        Execute(conn, tx, "DELETE FROM DocChunks WHERE LibraryName = @lib AND ApiVersion = @ver",
            ("@lib", libraryName), ("@ver", apiVersion));
        Execute(conn, tx, "DELETE FROM DocLibraries WHERE LibraryName = @lib AND Version = @ver",
            ("@lib", libraryName), ("@ver", apiVersion));
        Execute(conn, tx, "DELETE FROM IngestedPackages WHERE LibraryName = @lib AND ApiVersion = @ver",
            ("@lib", libraryName), ("@ver", apiVersion));

        tx.Commit();
    }

    /// <summary>Records that a package was ingested.</summary>
    public void RecordIngestion(string libraryName, string apiVersion, string packageVersion, int nodeCount)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO IngestedPackages (LibraryName, ApiVersion, PackageVersion, NodeCount)
            VALUES (@lib, @ver, @pkg, @count)
            ON CONFLICT(LibraryName, ApiVersion) DO UPDATE SET
                PackageVersion = @pkg, NodeCount = @count, IngestedAt = datetime('now')
            """;
        cmd.Parameters.AddWithValue("@lib", libraryName);
        cmd.Parameters.AddWithValue("@ver", apiVersion);
        cmd.Parameters.AddWithValue("@pkg", packageVersion);
        cmd.Parameters.AddWithValue("@count", nodeCount);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string name, object value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Result of repository ingestion (docs + optional TypeScript API).
/// </summary>
public sealed record RepositoryIngestResult(int DocChunkCount, int ApiNodeCount, int ApiRelationCount);
