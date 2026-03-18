using Microsoft.Data.Sqlite;
using McpDocMind.Lite.Constants;
using McpDocMind.Lite.Database;
using McpDocMind.Lite.Models;

namespace McpDocMind.Lite.Search;

/// <summary>
/// Graph queries using recursive CTEs (replaces SQL Server MATCH).
/// Handles type hierarchy, members, definitions, enums, comparisons.
/// </summary>
public sealed class GraphQueryService(AppDatabase db)
{
    public List<ApiNode> FindTypeByName(string pattern, string? apiVersion = null,
        string? nodeType = null, string? library = null, int limit = SearchConstants.DefaultTypeLimit)
    {
        using var conn = db.CreateConnection();
        // Convert * wildcard to SQL LIKE %
        var likePattern = pattern.Replace("*", "%");

        var sql = "SELECT * FROM ApiNodes WHERE (Name LIKE @pattern OR FullName LIKE @pattern)";
        if (apiVersion is not null) sql += " AND ApiVersion = @version";
        if (nodeType is not null) sql += " AND NodeType = @nodeType";
        if (library is not null) sql += " AND LibraryName = @library";
        sql += " ORDER BY Name LIMIT @limit";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@pattern", likePattern);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (apiVersion is not null) cmd.Parameters.AddWithValue("@version", apiVersion);
        if (nodeType is not null) cmd.Parameters.AddWithValue("@nodeType", nodeType);
        if (library is not null) cmd.Parameters.AddWithValue("@library", library);

        return ReadNodes(cmd);
    }

    public ApiNode? GetTypeDefinition(string typeName, string? apiVersion = null)
    {
        using var conn = db.CreateConnection();
        var sql = "SELECT * FROM ApiNodes WHERE FullName = @name";
        if (apiVersion is not null) sql += " AND ApiVersion = @version";
        sql += " LIMIT 1";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@name", typeName);
        if (apiVersion is not null) cmd.Parameters.AddWithValue("@version", apiVersion);

        var results = ReadNodes(cmd);
        return results.Count > 0 ? results[0] : null;
    }

    public List<ApiNode> GetTypeMembers(string typeName, string? memberType = null,
        string? apiVersion = null, int limit = SearchConstants.DefaultTypeLimit)
    {
        using var conn = db.CreateConnection();
        var sql = """
            SELECT c.* FROM ApiRelations r
            JOIN ApiNodes c ON c.Id = r.ChildId
            JOIN ApiNodes p ON p.Id = r.ParentId
            WHERE p.FullName = @parentName AND r.RelationType = 'Contains'
            """;
        if (memberType is not null) sql += " AND c.NodeType = @memberType";
        if (apiVersion is not null) sql += " AND c.ApiVersion = @version";
        sql += " ORDER BY c.NodeType, c.Name LIMIT @limit";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@parentName", typeName);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (memberType is not null) cmd.Parameters.AddWithValue("@memberType", memberType);
        if (apiVersion is not null) cmd.Parameters.AddWithValue("@version", apiVersion);

        return ReadNodes(cmd);
    }

    public List<ApiNode> GetConstructors(string typeName, string? apiVersion = null, int limit = 50)
    {
        return GetTypeMembers(typeName, "Constructor", apiVersion, limit);
    }

    public List<ApiNode> GetEnumValues(string enumName, string? apiVersion = null, int limit = 100)
    {
        return GetTypeMembers(enumName, "Field", apiVersion, limit);
    }

    /// <summary>
    /// Get all base types (parent classes) using recursive CTE.
    /// </summary>
    public List<ApiNode> GetBaseTypes(string typeName, string? apiVersion = null, int limit = 50)
    {
        using var conn = db.CreateConnection();
        var sql = """
            WITH RECURSIVE Hierarchy AS (
                SELECT r.ParentId, 1 as Depth
                FROM ApiRelations r
                JOIN ApiNodes child ON child.Id = r.ChildId
                WHERE child.FullName = @typeName AND r.RelationType = 'InheritsFrom'
                UNION ALL
                SELECT r.ParentId, h.Depth + 1
                FROM ApiRelations r
                JOIN Hierarchy h ON r.ChildId = h.ParentId
                WHERE r.RelationType = 'InheritsFrom' AND h.Depth < 20
            )
            SELECT n.* FROM ApiNodes n
            JOIN Hierarchy h ON n.Id = h.ParentId
            """;
        if (apiVersion is not null) sql += " WHERE n.ApiVersion = @version";
        sql += " ORDER BY h.Depth LIMIT @limit";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@typeName", typeName);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (apiVersion is not null) cmd.Parameters.AddWithValue("@version", apiVersion);

        return ReadNodes(cmd);
    }

    /// <summary>
    /// Get all subclasses that inherit from a given class.
    /// </summary>
    public List<ApiNode> GetSubclasses(string className, string? apiVersion = null, int limit = 100)
    {
        using var conn = db.CreateConnection();
        var sql = """
            WITH RECURSIVE Hierarchy AS (
                SELECT r.ChildId, 1 as Depth
                FROM ApiRelations r
                JOIN ApiNodes parent ON parent.Id = r.ParentId
                WHERE parent.FullName = @className AND r.RelationType = 'InheritsFrom'
                UNION ALL
                SELECT r.ChildId, h.Depth + 1
                FROM ApiRelations r
                JOIN Hierarchy h ON r.ParentId = h.ChildId
                WHERE r.RelationType = 'InheritsFrom' AND h.Depth < 20
            )
            SELECT n.* FROM ApiNodes n
            JOIN Hierarchy h ON n.Id = h.ChildId
            """;
        if (apiVersion is not null) sql += " WHERE n.ApiVersion = @version";
        sql += " LIMIT @limit";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@className", className);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (apiVersion is not null) cmd.Parameters.AddWithValue("@version", apiVersion);

        return ReadNodes(cmd);
    }

    /// <summary>
    /// Get all classes that implement a specific interface.
    /// </summary>
    public List<ApiNode> GetImplementors(string interfaceName, int limit = 100)
    {
        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.* FROM ApiRelations r
            JOIN ApiNodes n ON n.Id = r.ChildId
            JOIN ApiNodes iface ON iface.Id = r.ParentId
            WHERE iface.FullName = @ifaceName AND r.RelationType = 'Implements'
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@ifaceName", interfaceName);
        cmd.Parameters.AddWithValue("@limit", limit);

        return ReadNodes(cmd);
    }

    /// <summary>
    /// Get all interfaces implemented by a specific type.
    /// </summary>
    public List<ApiNode> GetInterfaces(string typeName, string? apiVersion = null, int limit = 50)
    {
        using var conn = db.CreateConnection();
        var sql = """
            SELECT n.* FROM ApiRelations r
            JOIN ApiNodes n ON n.Id = r.ParentId
            JOIN ApiNodes child ON child.Id = r.ChildId
            WHERE child.FullName = @typeName AND r.RelationType = 'Implements'
            """;
        if (apiVersion is not null) sql += " AND n.ApiVersion = @version";
        sql += " LIMIT @limit";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@typeName", typeName);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (apiVersion is not null) cmd.Parameters.AddWithValue("@version", apiVersion);

        return ReadNodes(cmd);
    }

    /// <summary>
    /// Get all types within a namespace.
    /// </summary>
    public List<ApiNode> GetTypesInNamespace(string ns, string? apiVersion = null,
        string? library = null, int limit = SearchConstants.DefaultTypeLimit)
    {
        using var conn = db.CreateConnection();
        var sql = "SELECT * FROM ApiNodes WHERE Namespace = @ns";
        if (apiVersion is not null) sql += " AND ApiVersion = @version";
        if (library is not null) sql += " AND LibraryName = @library";
        sql += " ORDER BY NodeType, Name LIMIT @limit";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@ns", ns);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (apiVersion is not null) cmd.Parameters.AddWithValue("@version", apiVersion);
        if (library is not null) cmd.Parameters.AddWithValue("@library", library);

        return ReadNodes(cmd);
    }

    /// <summary>
    /// List all distinct namespaces.
    /// </summary>
    public List<string> ListNamespaces(string? library = null, int limit = 200)
    {
        using var conn = db.CreateConnection();
        var sql = "SELECT DISTINCT Namespace FROM ApiNodes WHERE Namespace IS NOT NULL";
        if (library is not null) sql += " AND LibraryName = @library";
        sql += " ORDER BY Namespace LIMIT @limit";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (library is not null) cmd.Parameters.AddWithValue("@library", library);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    /// <summary>
    /// List all libraries with item counts.
    /// </summary>
    public List<LibraryInfo> ListLibraries()
    {
        using var conn = db.CreateConnection();
        var results = new List<LibraryInfo>();

        // API libraries
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT LibraryName, ApiVersion, COUNT(*) as Count
                FROM ApiNodes WHERE LibraryName IS NOT NULL
                GROUP BY LibraryName, ApiVersion
                ORDER BY LibraryName, ApiVersion
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new LibraryInfo
                {
                    LibraryName = reader.GetString(0),
                    ApiVersion = reader.GetString(1),
                    ItemCount = reader.GetInt32(2),
                    Type = "api"
                });
            }
        }

        // Doc libraries
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT LibraryName, Version, ChunkCount FROM DocLibraries ORDER BY LibraryName
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new LibraryInfo
                {
                    LibraryName = reader.GetString(0),
                    ApiVersion = reader.GetString(1),
                    ItemCount = reader.GetInt32(2),
                    Type = "docs"
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Delete a library and all associated data.
    /// </summary>
    public void DeleteLibrary(string libraryName, string apiVersion)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Delete embeddings first (FK)
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM DocEmbeddings WHERE ChunkId IN
                    (SELECT Id FROM DocChunks WHERE LibraryName=@lib AND ApiVersion=@ver);
                DELETE FROM ApiEmbeddings WHERE NodeId IN
                    (SELECT Id FROM ApiNodes WHERE LibraryName=@lib AND ApiVersion=@ver);
                """;
            cmd.Parameters.AddWithValue("@lib", libraryName);
            cmd.Parameters.AddWithValue("@ver", apiVersion);
            cmd.ExecuteNonQuery();
        }

        // Delete relations
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM ApiRelations WHERE ParentId IN
                    (SELECT Id FROM ApiNodes WHERE LibraryName=@lib AND ApiVersion=@ver)
                OR ChildId IN
                    (SELECT Id FROM ApiNodes WHERE LibraryName=@lib AND ApiVersion=@ver);
                """;
            cmd.Parameters.AddWithValue("@lib", libraryName);
            cmd.Parameters.AddWithValue("@ver", apiVersion);
            cmd.ExecuteNonQuery();
        }

        // Delete core data
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM DocChunks WHERE LibraryName=@lib AND ApiVersion=@ver;
                DELETE FROM ApiNodes WHERE LibraryName=@lib AND ApiVersion=@ver;
                DELETE FROM DocLibraries WHERE LibraryName=@lib AND Version=@ver;
                """;
            cmd.Parameters.AddWithValue("@lib", libraryName);
            cmd.Parameters.AddWithValue("@ver", apiVersion);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Rename a library (update LibraryName in all tables).
    /// </summary>
    public int RenameLibrary(string oldName, string apiVersion, string newName)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        var total = 0;

        // Update ApiNodes
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE ApiNodes SET LibraryName = @new WHERE LibraryName = @old AND ApiVersion = @ver";
            cmd.Parameters.AddWithValue("@new", newName);
            cmd.Parameters.AddWithValue("@old", oldName);
            cmd.Parameters.AddWithValue("@ver", apiVersion);
            total += cmd.ExecuteNonQuery();
        }

        // Update DocChunks
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE DocChunks SET LibraryName = @new WHERE LibraryName = @old AND ApiVersion = @ver";
            cmd.Parameters.AddWithValue("@new", newName);
            cmd.Parameters.AddWithValue("@old", oldName);
            cmd.Parameters.AddWithValue("@ver", apiVersion);
            total += cmd.ExecuteNonQuery();
        }

        // Update DocLibraries
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE DocLibraries SET LibraryName = @new WHERE LibraryName = @old AND Version = @ver";
            cmd.Parameters.AddWithValue("@new", newName);
            cmd.Parameters.AddWithValue("@old", oldName);
            cmd.Parameters.AddWithValue("@ver", apiVersion);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return total;
    }

    private static List<ApiNode> ReadNodes(SqliteCommand cmd)
    {
        var results = new List<ApiNode>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ApiNode
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                LibraryName = reader.GetString(reader.GetOrdinal("LibraryName")),
                ApiVersion = reader.GetString(reader.GetOrdinal("ApiVersion")),
                NodeType = reader.GetString(reader.GetOrdinal("NodeType")),
                FullName = reader.GetString(reader.GetOrdinal("FullName")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                Namespace = reader.IsDBNull(reader.GetOrdinal("Namespace")) ? null : reader.GetString(reader.GetOrdinal("Namespace")),
                Summary = reader.IsDBNull(reader.GetOrdinal("Summary")) ? null : reader.GetString(reader.GetOrdinal("Summary")),
                Declaration = reader.IsDBNull(reader.GetOrdinal("Declaration")) ? null : reader.GetString(reader.GetOrdinal("Declaration")),
                ReturnType = reader.IsDBNull(reader.GetOrdinal("ReturnType")) ? null : reader.GetString(reader.GetOrdinal("ReturnType")),
                Parameters = reader.IsDBNull(reader.GetOrdinal("Parameters")) ? null : reader.GetString(reader.GetOrdinal("Parameters")),
                ParentType = reader.IsDBNull(reader.GetOrdinal("ParentType")) ? null : reader.GetString(reader.GetOrdinal("ParentType"))
            });
        }
        return results;
    }
}
