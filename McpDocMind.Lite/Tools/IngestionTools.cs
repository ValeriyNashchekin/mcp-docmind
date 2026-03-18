using System.ComponentModel;
using McpDocMind.Lite.Ingestion;
using ModelContextProtocol.Server;

namespace McpDocMind.Lite.Tools;

/// <summary>
/// Note: Ingestion tools (repo, dll, md) were removed from MCP to prevent timeouts.
/// Use the CLI mode: McpDocMind.Lite.exe --ingest <mode> ...
/// </summary>
[McpServerToolType]
public sealed class IngestionTools
{
    // This class is now empty or can be removed if not needed for other purposes.
    // We keep it to avoid breaking reflection-based discovery if it expects this class.
}
