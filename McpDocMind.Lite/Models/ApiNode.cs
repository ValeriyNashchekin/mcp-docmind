namespace McpDocMind.Lite.Models;

/// <summary>
/// Represents a node in the API graph (class, method, property, etc.).
/// </summary>
public sealed class ApiNode
{
    public long Id { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Namespace { get; set; }
    public string? Summary { get; set; }
    public string? Declaration { get; set; }
    public string? ReturnType { get; set; }
    public string? Parameters { get; set; }
    public string? ParentType { get; set; }

    // Query-only properties (not persisted)
    public double? Distance { get; set; }
    public double Score { get; set; }
}
