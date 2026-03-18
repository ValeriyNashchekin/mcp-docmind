using System.Text.Json;
using System.Text.Json.Serialization;
using McpDocMind.Lite.Models;

namespace McpDocMind.Lite.Ingestion;

/// <summary>
/// Parses TypeScript repositories by running ts-morph via Node.js subprocess.
/// Maps extracted API structure to ApiNode/ApiRelation domain models.
/// </summary>
public sealed class TypeScriptParser
{
    private readonly NodeJsRuntime _runtime = new();
    private bool _initialized;

    /// <summary>
    /// Detects whether a directory contains TypeScript files worth parsing.
    /// </summary>
    public static bool DetectTypeScript(string repoDir)
    {
        // Check for tsconfig.json in common locations
        string[] tsconfigCandidates =
        [
            "tsconfig.json", "tsconfig.build.json",
            Path.Combine("src", "tsconfig.json"),
            Path.Combine("lib", "tsconfig.json"),
        ];

        foreach (var candidate in tsconfigCandidates)
        {
            if (File.Exists(Path.Combine(repoDir, candidate)))
                return true;
        }

        // Check for .d.ts files (excluding node_modules)
        if (Directory.EnumerateFiles(repoDir, "*.d.ts", SearchOption.AllDirectories)
            .Any(f => !f.Contains("node_modules") && !f.Contains(".git")))
            return true;

        // Check for .ts files
        if (Directory.EnumerateFiles(repoDir, "*.ts", SearchOption.AllDirectories)
            .Any(f => !f.Contains("node_modules") && !f.Contains(".git") && !f.EndsWith(".d.ts")))
            return true;

        // Check for JS with JSDoc (look for src/ directory with .js files)
        var srcDir = Path.Combine(repoDir, "src");
        if (Directory.Exists(srcDir) &&
            Directory.EnumerateFiles(srcDir, "*.js", SearchOption.AllDirectories).Any())
            return true;

        return false;
    }

    /// <summary>
    /// Parses a TypeScript repository and returns ApiNodes + ApiRelations.
    /// </summary>
    public async Task<TsExtractResult> ParseAsync(string repoDir, string libraryName,
        string apiVersion, CancellationToken ct = default)
    {
        if (!_initialized)
        {
            await _runtime.EnsureInstalledAsync(ct);
            _initialized = true;
        }

        var args = $"\"{repoDir}\" --library \"{libraryName}\"";
        var (stdout, stderr, exitCode) = await _runtime.RunScriptAsync(args, ct);

        // Log stderr (warnings, progress) to console
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine(stderr);

        if (exitCode != 0)
            throw new InvalidOperationException(
                $"TypeScript extraction failed (exit code {exitCode}): {stderr}");

        if (string.IsNullOrWhiteSpace(stdout))
            return TsExtractResult.Empty;

        // Deserialize JSON output
        var raw = JsonSerializer.Deserialize<TsRawResult>(stdout, JsonOptions);
        if (raw is null)
            return TsExtractResult.Empty;

        // Map to domain models
        return MapToApiNodes(raw, libraryName, apiVersion);
    }

    private static TsExtractResult MapToApiNodes(TsRawResult raw, string libraryName, string apiVersion)
    {
        var nodes = new List<ApiNode>(raw.Nodes.Count);
        var nodeSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var n in raw.Nodes)
        {
            if (string.IsNullOrEmpty(n.FullName) || !nodeSet.Add(n.FullName))
                continue;

            nodes.Add(new ApiNode
            {
                LibraryName = libraryName,
                ApiVersion = apiVersion,
                NodeType = MapNodeType(n.NodeType),
                FullName = n.FullName,
                Name = n.Name ?? n.FullName.Split('.').Last(),
                Namespace = n.Namespace,
                Summary = n.Summary,
                Declaration = n.Declaration,
                ReturnType = n.ReturnType,
                Parameters = n.Parameters,
                ParentType = n.ParentType,
            });
        }

        // Resolve relations — only keep those where both source and target exist
        var relations = new List<TsResolvedRelation>();
        foreach (var r in raw.Relations)
        {
            if (string.IsNullOrEmpty(r.Source) || string.IsNullOrEmpty(r.Target) || string.IsNullOrEmpty(r.Type))
                continue;

            // Source must exist in our node set
            if (!nodeSet.Contains(r.Source))
                continue;

            // Target must exist in our node set (drop cross-package references)
            if (!nodeSet.Contains(r.Target))
                continue;

            relations.Add(new TsResolvedRelation(r.Source, r.Target, r.Type));
        }

        return new TsExtractResult(nodes, relations);
    }

    private static string MapNodeType(string? tsType) => tsType switch
    {
        "Class" => "Class",
        "Interface" => "Interface",
        "Enum" => "Enum",
        "TypeAlias" => "TypeAlias",
        "Method" => "Method",
        "Property" => "Property",
        "Field" => "Field",
        "Constructor" => "Constructor",
        "Event" => "Event",
        "Namespace" => "Namespace",
        _ => "Class", // fallback
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ── JSON deserialization models ─────────────────────────────────────────────

internal sealed class TsRawResult
{
    [JsonPropertyName("nodes")]
    public List<TsRawNode> Nodes { get; set; } = [];

    [JsonPropertyName("relations")]
    public List<TsRawRelation> Relations { get; set; } = [];
}

internal sealed class TsRawNode
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("nodeType")]
    public string? NodeType { get; set; }

    [JsonPropertyName("declaration")]
    public string? Declaration { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("returnType")]
    public string? ReturnType { get; set; }

    [JsonPropertyName("parameters")]
    public string? Parameters { get; set; }

    [JsonPropertyName("parentType")]
    public string? ParentType { get; set; }
}

internal sealed class TsRawRelation
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

// ── Result models ───────────────────────────────────────────────────────────

public sealed record TsResolvedRelation(string SourceFullName, string TargetFullName, string RelationType);

public sealed class TsExtractResult(List<ApiNode> nodes, List<TsResolvedRelation> relations)
{
    public List<ApiNode> Nodes { get; } = nodes;
    public List<TsResolvedRelation> Relations { get; } = relations;

    public static TsExtractResult Empty { get; } = new([], []);
}
