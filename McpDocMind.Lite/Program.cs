using McpDocMind.Lite.Database;
using McpDocMind.Lite.Embeddings;
using McpDocMind.Lite.Ingestion;
using McpDocMind.Lite.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using System.Text.Json;

// ─── CLI Mode: --ingest dll|md|list ───
if (args.Length >= 1 && args[0] == "--ingest")
{
    await RunIngestionCli(args);
    return;
}

// ─── MCP Server Mode (default) ───

// Setup file logging for troubleshooting
var logPath = Path.Combine(AppContext.BaseDirectory, "debug.log");
try { File.WriteAllText(logPath, $"[DocMind] Started at {DateTime.Now}\n"); } catch { }

void Log(string msg) {
    try { File.AppendAllText(logPath, $"{msg}\n"); } catch { }
    // ALWAYS use Error for logs in MCP mode to avoid polluting stdout
    Console.Error.WriteLine(msg);
}

Log($"[DocMind] Startup arguments: {string.Join(" ", args)}");

var builder = Host.CreateApplicationBuilder(args);

// MCP server with stdio transport, auto-discover tools
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Core services
builder.Services.AddSingleton<AppDatabase>();
builder.Services.AddSingleton<EmbeddingService>();

// Search & ingestion (scoped per request)
builder.Services.AddScoped<HybridSearchService>();
builder.Services.AddScoped<GraphQueryService>();
builder.Services.AddScoped<IngestService>();

var app = builder.Build();

// Ensure database schema on startup
var db = app.Services.GetRequiredService<AppDatabase>();
db.EnsureCreated();

await app.RunAsync();

// ─── CLI Handler ───

static async Task RunIngestionCli(string[] args)
{
    if (args.Length < 2)
    {
        PrintUsage();
        Environment.Exit(1);
        return;
    }

    // Parse global paths for CLI mode
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--git-path") McpDocMind.Lite.Ingestion.GitParser.ConfiguredGitPath = args[i + 1];
        if (args[i] == "--node-path") McpDocMind.Lite.Ingestion.NodeJsRuntime.ConfiguredNodePath = args[i + 1];
    }

    var mode = args[1]; // "dll", "md", "repo", or "list"

    var database = new AppDatabase();
    database.EnsureCreated();

    // ─── List installed libraries ───
    if (mode == "list")
    {
        var graph = new GraphQueryService(database);
        var libs = graph.ListLibraries();
        Console.WriteLine(JsonSerializer.Serialize(libs, new JsonSerializerOptions { WriteIndented = true }));
        return;
    }

    // ─── Ingest DLL or Markdown ───
    if (args.Length < 5)
    {
        PrintUsage();
        Environment.Exit(1);
        return;
    }

    var path = args[2];
    var libName = args[3];
    var apiVer = args[4];

    // Parse optional flags
    string? xmlPath = null;
    string? nugetVersion = null;
    for (var i = 5; i < args.Length - 1; i++)
    {
        if (args[i] == "--xml") xmlPath = args[i + 1];
        if (args[i] == "--nuget-version") nugetVersion = args[i + 1];
    }

    var embeddings = new EmbeddingService();
    var ingest = new IngestService(database, embeddings);

    try
    {
        // Version-aware: check if same NuGet version already ingested
        if (nugetVersion is not null)
        {
            var existing = ingest.GetIngestedVersion(libName, apiVer);
            if (existing == nugetVersion)
            {
                Console.WriteLine($"SKIP: {libName} v{apiVer} already ingested with NuGet {nugetVersion}");
                return;
            }

            if (existing is not null)
            {
                Console.Error.WriteLine($"Upgrading {libName} v{apiVer}: {existing} → {nugetVersion}");
                ingest.PurgeLibrary(libName, apiVer);
            }
        }

        int count;
        if (mode == "dll")
        {
            Console.Error.WriteLine($"Ingesting DLL: {path} as '{libName}' v{apiVer}");
            count = await ingest.IngestDllAsync(path, libName, apiVer, xmlPath);
            Console.WriteLine($"OK: Ingested {count} API nodes");

            if (nugetVersion is not null)
                ingest.RecordIngestion(libName, apiVer, nugetVersion, count);
        }
        else if (mode == "md")
        {
            Console.Error.WriteLine($"Ingesting Markdown: {path} as '{libName}' v{apiVer}");
            count = await ingest.IngestMarkdownAsync(path, libName, apiVer);
            Console.WriteLine($"OK: Ingested {count} documentation chunks");

            if (nugetVersion is not null)
                ingest.RecordIngestion(libName, apiVer, nugetVersion, count);
        }
        else if (mode == "repo")
        {
            Console.Error.WriteLine($"Ingesting Repository: {path} as '{libName}' v{apiVer}");
            var result = await ingest.IngestRepositoryAsync(path, libName, apiVer);
            Console.WriteLine($"OK: Ingested {result.DocChunkCount} chunks, {result.ApiNodeCount} nodes");

            if (nugetVersion is not null)
                ingest.RecordIngestion(libName, apiVer, nugetVersion, result.ApiNodeCount);
        }
        else if (mode == "ts")
        {
            Console.Error.WriteLine($"Ingesting TypeScript: {path} as '{libName}' v{apiVer}");
            var tsParser = new TypeScriptParser();
            var tsResult = await tsParser.ParseAsync(path, libName, apiVer);
            var (nodeCount, relCount) = await ingest.IngestTypeScriptAsync(
                tsResult.Nodes, tsResult.Relations);
            Console.WriteLine($"OK: Ingested {nodeCount} API nodes, {relCount} relations");

            if (nugetVersion is not null)
                ingest.RecordIngestion(libName, apiVer, nugetVersion, nodeCount);
        }
        else
        {
            Console.Error.WriteLine($"Unknown mode: {mode}. Use 'dll', 'md', 'ts', or 'list'.");
            Environment.Exit(1);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.Exit(1);
    }
}

static void PrintUsage()
{
    Console.Error.WriteLine("McpDocMind CLI - Ingestion & Management");
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  --ingest repo <url> <libName> <apiVer> [docsPath] [branch]  - Ingest Git repository (Markdown + TS)");
    Console.Error.WriteLine("  --ingest dll  <path> <libName> <apiVer> [--xml <path>]      - Ingest .NET DLL & XML docs");
    Console.Error.WriteLine("  --ingest md   <path> <libName> <apiVer>                     - Ingest Markdown directory/file");
    Console.Error.WriteLine("  --ingest list                                               - List all ingested libraries");
    Console.Error.WriteLine("");
    Console.Error.WriteLine("Global Options:");
    Console.Error.WriteLine("  --git-path <path>   - Specify path to git.exe");
    Console.Error.WriteLine("  --node-path <path>  - Specify path to node.exe");
}
