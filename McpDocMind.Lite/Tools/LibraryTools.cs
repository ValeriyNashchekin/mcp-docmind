using System.ComponentModel;
using System.Text.Json;
using McpDocMind.Lite.Search;
using ModelContextProtocol.Server;

namespace McpDocMind.Lite.Tools;

[McpServerToolType]
public sealed class LibraryTools(GraphQueryService graph)
{
    [McpServerTool(Name = "list_libraries"), Description("List all libraries (API and documentation) with versions and item counts.")]
    public string ListLibraries()
    {
        var libs = graph.ListLibraries();
        if (libs.Count == 0) return "No libraries found.";
        return JsonSerializer.Serialize(libs, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "delete_library"), Description("Delete a specific library and all its associated data (nodes, chunks).")]
    public string DeleteLibrary(
        [Description("Library name")] string library_name,
        [Description("API version")] string api_version)
    {
        graph.DeleteLibrary(library_name, api_version);
        return $"Library '{library_name}' version '{api_version}' deleted successfully.";
    }

    [McpServerTool(Name = "rename_library"), Description("Rename a library. Updates the library name across all tables (API nodes, doc chunks, etc).")]
    public string RenameLibrary(
        [Description("Current library name")] string old_name,
        [Description("API version")] string api_version,
        [Description("New library name")] string new_name)
    {
        var count = graph.RenameLibrary(old_name, api_version, new_name);
        return $"Renamed '{old_name}' → '{new_name}' (version '{api_version}'). {count} records updated.";
    }
}
