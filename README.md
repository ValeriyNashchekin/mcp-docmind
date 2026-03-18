# McpDocMind

MCP server for searching .NET API documentation, Markdown docs, and TypeScript APIs from AI agents (Claude Code, Gemini CLI, etc.).

Ingests .NET DLLs, Git repositories, and Markdown into a local SQLite database. Exposes hybrid search (FTS5 + vector embeddings) via Model Context Protocol.

## Installation

```bash
dotnet tool install -g McpDocMind
mcp-docmind
```

The installer will:
1. Deploy the MCP server to `%LocalAppData%\McpDocMind`
2. Let you select libraries to ingest (Revit, AutoCAD, APS, etc.)
3. Register the server in Claude Code / Gemini CLI configs

## Manual Ingestion (CLI)

```powershell
# .NET DLL with XML docs
McpDocMind.Lite.exe --ingest dll C:\path\to\RevitAPI.dll Revit 2026 --xml C:\path\to\RevitAPI.xml

# Git repository (Markdown + TypeScript)
McpDocMind.Lite.exe --ingest repo https://github.com/angular/angular.git angular 19.0.0

# Markdown directory
McpDocMind.Lite.exe --ingest md C:\path\to\docs MyLib 1.0

# List ingested libraries
McpDocMind.Lite.exe --ingest list
```

## MCP Tools

| Tool | Description |
|------|-------------|
| `search_knowledge` | Hybrid search across all ingested content |
| `search_api` | Search API types and members |
| `find_type_by_name` | Find a type by exact or partial name |
| `get_type_members` | Get members of a specific type |
| `get_type_definition` | Full type definition with docs |
| `list_libraries` | List ingested libraries |
| `list_namespaces` | Browse namespace hierarchy |

## Prerequisites

- .NET 10 SDK
- Windows 10/11 (x64)
- Git (for repository ingestion)
- Node.js v18+ (for TypeScript API extraction)
