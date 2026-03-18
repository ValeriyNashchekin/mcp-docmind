# McpDocMind

**McpDocMind** is a high-performance local documentation-mind for AI agents (like Claude, Cursor, or Gemini CLI). It allows you to ingest massive documentation repositories, .NET DLLs, or Markdown files into a local SQLite database and search through them using hybrid search (FTS5 + Vector Embeddings) directly from your AI tools via the Model Context Protocol (MCP).

## Key Features

- **Hybrid Search:** Combines lightning-fast full-text search (SQLite FTS5) with semantic vector embeddings for precise results.
- **Multi-Source Ingestion:**
  - **Git Repositories:** Clones and parses Markdown and TypeScript/JS APIs.
  - **.NET DLLs:** Extracts API structures, types, and XML documentation.
  - **Markdown:** Ingests local files or directories.
- **Architectural Isolation:** 
  - **CLI Mode:** Used for heavy administrative tasks like ingestion and database management (avoids timeouts).
  - **MCP Mode:** Fast, read-only interface for AI agents to query documentation.
- **Security:** Built-in protection against Command Injection and safe resource management (auto-cleanup of temp files).
- **No-Config Discovery:** Automatically locates Git and Node.js runtimes on Windows.

## Installation

1. Download `McpDocMind.Setup.exe` from the `out/` directory.
2. Run the installer and select **Full Install / Update**.
3. The installer will automatically register McpDocMind in your AI tools (Claude Code, Gemini CLI, etc.).
4. Restart your AI tools to connect.

## How to Ingest Documentation

Ingestion is performed via the CLI to ensure reliability and progress tracking.

### Ingest a Git Repository
```powershell
McpDocMind.Lite.exe --ingest repo https://github.com/angular/angular.git angular 19.0.0 aio/content/guide
```

### Ingest .NET DLL with XML Docs
```powershell
McpDocMind.Lite.exe --ingest dll C:\path\to\RevitAPI.dll Revit 2026 --xml C:\path\to\RevitAPI.xml
```

### List Ingested Libraries
```powershell
McpDocMind.Lite.exe --ingest list
```

## Using with AI Agents

Once ingested, the documentation is available to your AI agent through standard MCP tools:

- `search`: Hybrid search across all ingested content.
- `list_namespaces`: Explore the API structure.
- `research_type`: Get detailed information about a specific class or interface.
- `list_libraries`: See what's currently stored in your "mind".

## Prerequisites

- **Windows 10/11** (x64)
- **Git** (installed and in PATH, or auto-detected in standard locations)
- **Node.js v18+** (for TypeScript/JS API extraction)

## Developer Mode

To build from source:
1. Clone the repository.
2. Run `.\build-setup.ps1` to create the installer.
3. Find the output in `out\McpDocMind.Setup.exe`.

---
*Created by Gemini CLI - Powering your local documentation intelligence.*
