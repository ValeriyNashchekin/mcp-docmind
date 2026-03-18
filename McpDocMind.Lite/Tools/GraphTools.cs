using System.ComponentModel;
using System.Text.Json;
using McpDocMind.Lite.Search;
using McpDocMind.Lite.Models;
using ModelContextProtocol.Server;

namespace McpDocMind.Lite.Tools;

[McpServerToolType]
public sealed class GraphTools(GraphQueryService graph)
{
    [McpServerTool(Name = "find_type_by_name"), Description("Find types by name pattern (supports * wildcard). Optional filter by node type.")]
    public string FindTypeByName(
        [Description("Name pattern with * wildcard (e.g. '*Wall*')")] string pattern,
        [Description("Filter by API version (optional)")] string? api_version = null,
        [Description("Filter by node type: Class, Interface, Method, Property, Field, Event, Constructor, Enum, Struct, Delegate (optional)")] string? node_type = null,
        [Description("Filter by library name (optional)")] string? library = null,
        [Description("Max results (default: 100)")] int limit = 100)
    {
        var nodes = graph.FindTypeByName(pattern, api_version, node_type, library, limit);
        return FormatNodes(nodes);
    }

    [McpServerTool(Name = "get_type_definition"), Description("Get full definition and documentation for a specific .NET type.")]
    public string GetTypeDefinition(
        [Description("The fully qualified type name")] string type_name,
        [Description("Filter by API version (optional)")] string? api_version = null)
    {
        var node = graph.GetTypeDefinition(type_name, api_version);
        return node is null ? "Type not found." : FormatNode(node);
    }

    [McpServerTool(Name = "get_type_members"), Description("Get all members (methods, properties, events) of a specific type. Optional filter by member_type.")]
    public string GetTypeMembers(
        [Description("The fully qualified type name")] string type_name,
        [Description("Filter by member type: Method, Property, Event, Constructor (optional)")] string? member_type = null,
        [Description("Filter by API version (optional)")] string? api_version = null,
        [Description("Max results (default: 100)")] int limit = 100)
    {
        var members = graph.GetTypeMembers(type_name, member_type, api_version, limit);
        return FormatNodes(members);
    }

    [McpServerTool(Name = "get_constructors"), Description("Get all constructors for a specific type.")]
    public string GetConstructors(
        [Description("The fully qualified type name")] string type_name,
        [Description("Filter by API version (optional)")] string? api_version = null,
        [Description("Max results (default: 50)")] int limit = 50)
    {
        var ctors = graph.GetConstructors(type_name, api_version, limit);
        return FormatNodes(ctors);
    }

    [McpServerTool(Name = "get_enum_values"), Description("Get all values/names for a specific enum type.")]
    public string GetEnumValues(
        [Description("The fully qualified enum name")] string enum_name,
        [Description("Filter by API version (optional)")] string? api_version = null,
        [Description("Max results (default: 100)")] int limit = 100)
    {
        var values = graph.GetEnumValues(enum_name, api_version, limit);
        return FormatNodes(values);
    }

    [McpServerTool(Name = "get_base_types"), Description("Get all base types (parent classes) in the inheritance hierarchy.")]
    public string GetBaseTypes(
        [Description("The fully qualified type name")] string type_name,
        [Description("Filter by API version (optional)")] string? api_version = null,
        [Description("Max results (default: 50)")] int limit = 50)
    {
        var types = graph.GetBaseTypes(type_name, api_version, limit);
        return FormatNodes(types);
    }

    [McpServerTool(Name = "get_subclasses"), Description("Find all classes that inherit from a specific class.")]
    public string GetSubclasses(
        [Description("The fully qualified class name")] string class_name,
        [Description("Max results (default: 100)")] int limit = 100)
    {
        var types = graph.GetSubclasses(class_name, limit: limit);
        return FormatNodes(types);
    }

    [McpServerTool(Name = "get_implementors"), Description("Find all classes that implement a specific interface.")]
    public string GetImplementors(
        [Description("The fully qualified interface name")] string interface_name,
        [Description("Max results (default: 100)")] int limit = 100)
    {
        var types = graph.GetImplementors(interface_name, limit);
        return FormatNodes(types);
    }

    [McpServerTool(Name = "get_interfaces"), Description("Get all interfaces implemented by a specific type.")]
    public string GetInterfaces(
        [Description("The fully qualified type name")] string type_name,
        [Description("Filter by API version (optional)")] string? api_version = null,
        [Description("Max results (default: 50)")] int limit = 50)
    {
        var types = graph.GetInterfaces(type_name, api_version, limit);
        return FormatNodes(types);
    }

    [McpServerTool(Name = "get_types_in_namespace"), Description("Get all types within a specific namespace.")]
    public string GetTypesInNamespace(
        [Description("The namespace to search in")] string @namespace,
        [Description("Filter by API version (optional)")] string? api_version = null,
        [Description("Filter by library name (optional)")] string? library = null,
        [Description("Max results (default: 100)")] int limit = 100)
    {
        var types = graph.GetTypesInNamespace(@namespace, api_version, library, limit);
        return FormatNodes(types);
    }

    [McpServerTool(Name = "list_namespaces"), Description("List all namespaces in a library or across all libraries.")]
    public string ListNamespaces(
        [Description("Filter by library name (optional)")] string? library = null,
        [Description("Max results (default: 200)")] int limit = 200)
    {
        var namespaces = graph.ListNamespaces(library, limit);
        return JsonSerializer.Serialize(namespaces, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "research_type"), Description("Deep research a .NET type: returns definition, all members (properties, methods, events), constructors, base types, and interfaces in ONE call. Use this instead of calling get_type_definition + get_type_members + get_constructors + get_base_types separately. For enums, returns all values.")]
    public string ResearchType(
        [Description("The fully qualified type name (e.g. 'Autodesk.Revit.DB.Material')")] string type_name,
        [Description("Filter by API version (e.g. '26.0.4.409'). Optional.")] string? api_version = null,
        [Description("Include inherited members. Default: false")] bool include_inherited = false)
    {
        var def = graph.GetTypeDefinition(type_name, api_version);
        if (def is null) return JsonSerializer.Serialize(new { error = $"Type '{type_name}' not found" });

        // For enums, return values
        if (def.NodeType == "Enum")
        {
            var vals = graph.GetEnumValues(type_name, api_version, 200);
            return JsonSerializer.Serialize(new
            {
                fullName = def.FullName,
                nodeType = def.NodeType,
                declaration = def.Declaration,
                summary = CleanSummary(def.Summary),
                library = def.LibraryName,
                version = def.ApiVersion,
                values = vals.Select(v => v.Name).ToList()
            });
        }

        var props = graph.GetTypeMembers(type_name, "Property", api_version, 100);
        var methods = graph.GetTypeMembers(type_name, "Method", api_version, 100);
        var ctors = graph.GetConstructors(type_name, api_version, 20);
        var baseTypes = graph.GetBaseTypes(type_name, api_version, 10);
        var ifaces = graph.GetInterfaces(type_name, api_version, 20);
        var events = graph.GetTypeMembers(type_name, "Event", api_version, 50);

        return JsonSerializer.Serialize(new
        {
            fullName = def.FullName,
            nodeType = def.NodeType,
            declaration = def.Declaration,
            summary = CleanSummary(def.Summary),
            library = def.LibraryName,
            version = def.ApiVersion,
            base_types = baseTypes.Select(n => n.FullName).ToList(),
            interfaces = ifaces.Select(n => n.FullName).ToList(),
            constructors = ctors.Select(n => n.Declaration).ToList(),
            properties = props.Select(n => n.Declaration).ToList(),
            methods = methods.Select(n => n.Declaration).ToList(),
            events = events.Select(n => n.Declaration).ToList()
        });
    }

    private static string FormatNodes(List<ApiNode> nodes)
    {
        if (nodes.Count == 0) return "No results found.";
        return JsonSerializer.Serialize(nodes.Select(n => new
        {
            n.FullName, n.Name, n.NodeType,
            Summary = CleanSummary(n.Summary),
            n.Declaration,
            n.LibraryName, n.ApiVersion
        }), new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatNode(ApiNode n) =>
        JsonSerializer.Serialize(new
        {
            n.FullName, n.Name, n.NodeType, n.Namespace,
            Summary = CleanSummary(n.Summary),
            n.Declaration, n.ReturnType, n.Parameters,
            n.ParentType, n.LibraryName, n.ApiVersion
        }, new JsonSerializerOptions { WriteIndented = true });

    private static string? CleanSummary(string? summary)
    {
        if (string.IsNullOrEmpty(summary)) return summary;

        var remarksIdx = summary.IndexOf("\nRemarks:", StringComparison.OrdinalIgnoreCase);
        if (remarksIdx < 0) remarksIdx = summary.IndexOf("\r\nRemarks:", StringComparison.OrdinalIgnoreCase);
        if (remarksIdx > 0) summary = summary[..remarksIdx];

        summary = summary.Replace("\r\n", " ").Replace("\n", " ").Trim();
        if (summary.Length > 200) summary = summary[..197] + "...";

        return summary;
    }
}
