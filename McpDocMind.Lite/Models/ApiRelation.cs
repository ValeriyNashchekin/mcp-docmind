namespace McpDocMind.Lite.Models;

/// <summary>
/// Represents a relationship between two API nodes (inheritance, implementation, containment).
/// </summary>
public sealed class ApiRelation
{
    public long Id { get; set; }
    public long ParentId { get; set; }
    public long ChildId { get; set; }
    public string RelationType { get; set; } = string.Empty;
}
