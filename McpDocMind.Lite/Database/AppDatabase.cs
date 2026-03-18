using Microsoft.Data.Sqlite;
using McpDocMind.Lite.Constants;

namespace McpDocMind.Lite.Database;

/// <summary>
/// SQLite database with FTS5 and sqlite-vec for hybrid search.
/// Manages schema creation and provides connections.
/// </summary>
public sealed class AppDatabase
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public AppDatabase()
    {
        // Store DB in AppData or next to the executable
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "McpDocMind");
        Directory.CreateDirectory(appData);
        _dbPath = Path.Combine(appData, "docmind.db");
        // Add Busy Timeout and optimize connection string
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared;Default Timeout=30";
    }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // Enable WAL mode for better concurrent read performance
        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-64000;";
        walCmd.ExecuteNonQuery();

        return conn;
    }

    /// <summary>
    /// Creates all tables, FTS5 indexes, and triggers on first run.
    /// </summary>
    public void EnsureCreated()
    {
        using var conn = CreateConnection();

        var ddl = """
            -- Core: API Nodes
            CREATE TABLE IF NOT EXISTS ApiNodes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                LibraryName TEXT NOT NULL,
                ApiVersion TEXT NOT NULL,
                NodeType TEXT NOT NULL,
                FullName TEXT NOT NULL,
                Name TEXT NOT NULL,
                Namespace TEXT,
                Summary TEXT,
                Declaration TEXT,
                ReturnType TEXT,
                Parameters TEXT,
                ParentType TEXT,
                UNIQUE(LibraryName, ApiVersion, FullName)
            );

            -- Core: Relations (replaces SQL Server Graph Tables)
            CREATE TABLE IF NOT EXISTS ApiRelations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ParentId INTEGER NOT NULL REFERENCES ApiNodes(Id) ON DELETE CASCADE,
                ChildId INTEGER NOT NULL REFERENCES ApiNodes(Id) ON DELETE CASCADE,
                RelationType TEXT NOT NULL
            );

            -- Core: Doc Chunks
            CREATE TABLE IF NOT EXISTS DocChunks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                LibraryName TEXT NOT NULL,
                ApiVersion TEXT NOT NULL,
                Source TEXT NOT NULL,
                Section TEXT,
                Content TEXT NOT NULL,
                ChunkIndex INTEGER DEFAULT 0,
                HasCode INTEGER DEFAULT 0,
                ContentHash TEXT,
                Tokens INTEGER DEFAULT 0,
                UNIQUE(LibraryName, ApiVersion, ContentHash)
            );

            -- Core: Doc Libraries (metadata)
            CREATE TABLE IF NOT EXISTS DocLibraries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                LibraryName TEXT NOT NULL,
                Version TEXT NOT NULL,
                Description TEXT,
                ChunkCount INTEGER DEFAULT 0,
                UNIQUE(LibraryName, Version)
            );

            -- Indexes
            CREATE INDEX IF NOT EXISTS idx_nodes_library ON ApiNodes(LibraryName, ApiVersion);
            CREATE INDEX IF NOT EXISTS idx_nodes_fullname ON ApiNodes(FullName);
            CREATE INDEX IF NOT EXISTS idx_nodes_name ON ApiNodes(Name);
            CREATE INDEX IF NOT EXISTS idx_nodes_namespace ON ApiNodes(Namespace);
            CREATE INDEX IF NOT EXISTS idx_nodes_type ON ApiNodes(NodeType);
            CREATE INDEX IF NOT EXISTS idx_relations_parent ON ApiRelations(ParentId);
            CREATE INDEX IF NOT EXISTS idx_relations_child ON ApiRelations(ChildId);
            CREATE INDEX IF NOT EXISTS idx_relations_type ON ApiRelations(RelationType);
            CREATE INDEX IF NOT EXISTS idx_chunks_library ON DocChunks(LibraryName, ApiVersion);
            CREATE INDEX IF NOT EXISTS idx_chunks_hash ON DocChunks(ContentHash);
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();

        // FTS5 virtual tables (separate statements required)
        ExecuteIfNotExists(conn,
            "ApiNodes_fts",
            """
            CREATE VIRTUAL TABLE ApiNodes_fts USING fts5(
                FullName, Name, Summary, Declaration,
                content='ApiNodes', content_rowid='Id'
            )
            """);

        ExecuteIfNotExists(conn,
            "DocChunks_fts",
            """
            CREATE VIRTUAL TABLE DocChunks_fts USING fts5(
                Source, Section, Content,
                content='DocChunks', content_rowid='Id'
            )
            """);

        // FTS5 triggers: keep FTS index in sync with base tables
        var triggers = """
            CREATE TRIGGER IF NOT EXISTS ApiNodes_ai AFTER INSERT ON ApiNodes BEGIN
                INSERT INTO ApiNodes_fts(rowid, FullName, Name, Summary, Declaration)
                VALUES (new.Id, new.FullName, new.Name, new.Summary, new.Declaration);
            END;

            CREATE TRIGGER IF NOT EXISTS ApiNodes_ad AFTER DELETE ON ApiNodes BEGIN
                INSERT INTO ApiNodes_fts(ApiNodes_fts, rowid, FullName, Name, Summary, Declaration)
                VALUES ('delete', old.Id, old.FullName, old.Name, old.Summary, old.Declaration);
            END;

            CREATE TRIGGER IF NOT EXISTS DocChunks_ai AFTER INSERT ON DocChunks BEGIN
                INSERT INTO DocChunks_fts(rowid, Source, Section, Content)
                VALUES (new.Id, new.Source, new.Section, new.Content);
            END;

            CREATE TRIGGER IF NOT EXISTS DocChunks_ad AFTER DELETE ON DocChunks BEGIN
                INSERT INTO DocChunks_fts(DocChunks_fts, rowid, Source, Section, Content)
                VALUES ('delete', old.Id, old.Source, old.Section, old.Content);
            END;
            """;

        using var triggerCmd = conn.CreateCommand();
        triggerCmd.CommandText = triggers;
        triggerCmd.ExecuteNonQuery();

        // Note: sqlite-vec tables will be created when the extension is loaded.
        // For now, we use a simple embeddings table as fallback.
        var vecFallback = """
            CREATE TABLE IF NOT EXISTS ApiEmbeddings (
                NodeId INTEGER PRIMARY KEY REFERENCES ApiNodes(Id) ON DELETE CASCADE,
                Embedding BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DocEmbeddings (
                ChunkId INTEGER PRIMARY KEY REFERENCES DocChunks(Id) ON DELETE CASCADE,
                Embedding BLOB NOT NULL
            );

            -- Tracks exact NuGet/source version ingested per library
            CREATE TABLE IF NOT EXISTS IngestedPackages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                LibraryName TEXT NOT NULL,
                ApiVersion TEXT NOT NULL,
                PackageVersion TEXT NOT NULL,
                IngestedAt TEXT NOT NULL DEFAULT (datetime('now')),
                NodeCount INTEGER DEFAULT 0,
                UNIQUE(LibraryName, ApiVersion)
            );
            """;

        using var vecCmd = conn.CreateCommand();
        vecCmd.CommandText = vecFallback;
        vecCmd.ExecuteNonQuery();
    }

    private static void ExecuteIfNotExists(SqliteConnection conn, string tableName, string createSql)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
        checkCmd.Parameters.AddWithValue("@name", tableName);
        var exists = Convert.ToInt64(checkCmd.ExecuteScalar()) > 0;
        if (exists) return;

        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = createSql;
        createCmd.ExecuteNonQuery();
    }
}
